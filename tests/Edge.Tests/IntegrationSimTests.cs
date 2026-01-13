using ZebraBridge.Edge;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Orchestrator;
using ZebraBridge.Edge.Outbox;
using ZebraBridge.Edge.Stability;
using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class IntegrationSimTests
{
    [Fact]
    public async Task PlacementToPrintToClear()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"edge-{Guid.NewGuid():N}.db");
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, settings);

        var batchStore = new BatchStateStore(dbPath);
        var printStore = new PrintOutboxStore(dbPath);
        var erpStore = new ErpOutboxStore(dbPath);

        var controlQueue = new Queue<FsmEvent>();
        void Enqueue(FsmEvent ev) => controlQueue.Enqueue(ev);

        var orchestrator = new EventOrchestrator(
            batchStore,
            printStore,
            erpStore,
            maxErpQueue: 1000,
            supportsStatusProbe: true,
            controlEnqueue: Enqueue);

        orchestrator.Initialize();
        await orchestrator.HandleBatchStartAsync(new BatchStartEvent("dev-1", "batch-1", "prod-1", 1.0, 0));
        fsm.Handle(new BatchStartEvent("dev-1", "batch-1", "prod-1", 1.0, 0));

        var simulator = new ScaleSimulator()
            .AddSegment(duration: 1.0, weight: 0.0)
            .AddSegment(duration: 2.0, weight: 5.0)
            .AddSegment(duration: 1.0, weight: 0.0);

        foreach (var sample in simulator.Run())
        {
            var actions = fsm.Handle(new SampleEvent(sample));
            foreach (var action in actions)
            {
                await orchestrator.HandleActionAsync(action, CancellationToken.None);
            }

            while (controlQueue.Count > 0)
            {
                var ev = controlQueue.Dequeue();
                fsm.Handle(ev);
            }
        }

        if (controlQueue.Count > 0)
        {
            var ev = controlQueue.Dequeue();
            fsm.Handle(ev);
        }

        var printJob = await printStore.FetchNextAsync(0);
        if (printJob != null)
        {
            fsm.Handle(new PrinterReceivedEvent(printJob.EventId, simulator.NowSeconds));
            fsm.Handle(new PrinterCompletedEvent(printJob.EventId, simulator.NowSeconds + 0.5));
        }

        for (var i = 0; i < 10; i++)
        {
            var sample = new WeightSample(0.0, "kg", simulator.NowSeconds + (i + 1) * 0.1);
            fsm.Handle(new SampleEvent(sample));
        }

        Assert.Equal(FsmState.WaitEmpty, fsm.State);
    }

    [Fact]
    public async Task ScanReconRequiredBlocksErpUntilScanRecon()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"edge-{Guid.NewGuid():N}.db");
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, settings);

        var batchStore = new BatchStateStore(dbPath);
        var printStore = new PrintOutboxStore(dbPath);
        var erpStore = new ErpOutboxStore(dbPath);

        var controlQueue = new Queue<FsmEvent>();
        void Enqueue(FsmEvent ev) => controlQueue.Enqueue(ev);

        var orchestrator = new EventOrchestrator(
            batchStore,
            printStore,
            erpStore,
            maxErpQueue: 1000,
            supportsStatusProbe: true,
            controlEnqueue: Enqueue);

        orchestrator.Initialize();
        var batchStart = new BatchStartEvent("dev-1", "batch-1", "prod-1", 1.0, 0);
        await orchestrator.HandleBatchStartAsync(batchStart);
        fsm.Handle(batchStart);

        var simulator = new ScaleSimulator()
            .AddSegment(duration: 1.0, weight: 0.0)
            .AddSegment(duration: 2.0, weight: 5.0);

        foreach (var sample in simulator.Run())
        {
            var actions = fsm.Handle(new SampleEvent(sample));
            foreach (var action in actions)
            {
                await orchestrator.HandleActionAsync(action, CancellationToken.None);
            }

            while (controlQueue.Count > 0)
            {
                var ev = controlQueue.Dequeue();
                fsm.Handle(ev);
            }
        }

        var printJob = await printStore.FetchNextAsync(0);
        Assert.NotNull(printJob);

        var eventId = printJob!.EventId;
        fsm.Handle(new ScanReconRequiredEvent(eventId, simulator.NowSeconds));
        Assert.Equal(FsmState.ScanReconRequired, fsm.State);

        var erpClient = new FakeErpClient();
        var erpSignal = new SemaphoreSlim(0);
        var erpWorker = new ErpWorker(erpStore, printStore, erpClient, erpSignal);

        using var cts = new CancellationTokenSource();
        var erpTask = erpWorker.RunAsync(cts.Token);
        erpSignal.Release();

        await Task.Delay(50);
        Assert.Equal(0, erpClient.CallCount);

        var reconActions = fsm.Handle(new ScanReconEvent(eventId, simulator.NowSeconds + 0.5));
        foreach (var action in reconActions)
        {
            await orchestrator.HandleActionAsync(action, CancellationToken.None);
        }

        await erpStore.MarkWaitPrintAsync(eventId, 0, 0);
        erpSignal.Release();
        await WaitForConditionAsync(() => Task.FromResult(erpClient.CallCount == 1), timeoutMs: 1000);

        cts.Cancel();
        await IgnoreCancellationAsync(erpTask);

        Assert.Equal(1, erpClient.CallCount);
    }

    private static StabilitySettings CreateSettings()
    {
        return new StabilitySettings(
            sigma: 0.01,
            res: 0.01,
            windowSeconds: 0.5,
            eps: 0.03,
            epsAlign: 0.06,
            emptyThreshold: 0.05,
            placementMinWeight: 1.0,
            slopeLimit: 0.02);
    }

    private sealed class ScaleSimulator
    {
        private readonly List<(double Duration, double Weight)> _segments = new();
        public double NowSeconds { get; private set; }

        public ScaleSimulator AddSegment(double duration, double weight)
        {
            _segments.Add((duration, weight));
            return this;
        }

        public IEnumerable<WeightSample> Run(double rateHz = 10)
        {
            var dt = 1.0 / rateHz;
            foreach (var segment in _segments)
            {
                var end = NowSeconds + segment.Duration;
                while (NowSeconds < end)
                {
                    NowSeconds += dt;
                    yield return new WeightSample(segment.Weight, "kg", NowSeconds);
                }
            }
        }
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(10);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FakeErpClient : IErpClient
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<ErpResult> PostEventAsync(string payloadJson, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(ErpResult.Ok);
        }
    }
}
