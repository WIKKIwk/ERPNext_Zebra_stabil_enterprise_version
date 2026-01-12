using System.Threading.Channels;
using ZebraBridge.Edge.Adapters;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Orchestrator;
using ZebraBridge.Edge.Outbox;
using ZebraBridge.Edge.Runtime;
using ZebraBridge.Edge.Stability;

namespace ZebraBridge.Edge;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        var dbPath = args[0];
        var cts = new CancellationTokenSource();

        var settings = new StabilitySettings(
            sigma: 0.01,
            res: 0.01,
            windowSeconds: 1.0,
            eps: 0.03,
            epsAlign: 0.06,
            emptyThreshold: 0.05,
            placementMinWeight: 1.0,
            slopeLimit: 0.02);

        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, settings);

        var actionChannel = Channel.CreateUnbounded<FsmAction>();
        var fsmSignal = new SemaphoreSlim(0);
        var alarmSink = new NullAlarmSink();
        var fsmLoop = new FsmEventLoop(fsm, actionChannel.Writer, 4096, fsmSignal, alarmSink);

        var batchStore = new BatchStateStore(dbPath);
        var printOutbox = new PrintOutboxStore(dbPath);
        var erpOutbox = new ErpOutboxStore(dbPath);

        var printer = new NullPrinterTransport();
        var orchestrator = new EventOrchestrator(
            batchStore,
            printOutbox,
            erpOutbox,
            maxErpQueue: 10000,
            supportsStatusProbe: printer.SupportsStatusProbe,
            controlEnqueue: fsmLoop.EnqueueControl);

        orchestrator.Initialize();

        var printSignal = new SemaphoreSlim(0);
        var erpSignal = new SemaphoreSlim(0);

        var printWorker = new PrintWorker(printOutbox, printer, fsmLoop.EnqueueControl, printSignal);
        var erpWorker = new ErpWorker(erpOutbox, new NullErpClient(), erpSignal);

        var scaleAdapter = new NullScaleAdapter();
        var scaleReadLoop = new ScaleReadLoop(scaleAdapter, fsmLoop, () => false);

        var fsmTask = fsmLoop.RunAsync(cts.Token);
        var orchestratorTask = RunOrchestratorAsync(actionChannel.Reader, orchestrator, cts.Token);
        var printTask = printWorker.RunAsync(cts.Token);
        var erpTask = erpWorker.RunAsync(cts.Token);
        var scaleTask = scaleReadLoop.RunAsync(cts.Token);

        await Task.WhenAll(fsmTask, orchestratorTask, printTask, erpTask, scaleTask);
    }

    private static async Task RunOrchestratorAsync(ChannelReader<FsmAction> reader, EventOrchestrator orchestrator, CancellationToken token)
    {
        await foreach (var action in reader.ReadAllAsync(token))
        {
            await orchestrator.HandleActionAsync(action, token);
        }
    }

    private sealed class NullScaleAdapter : IScaleAdapter
    {
        public async IAsyncEnumerable<WeightSample> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                yield return new WeightSample(0, "kg", NowSeconds());
            }
        }

        private static double NowSeconds()
        {
            return (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private sealed class NullPrinterTransport : IPrinterTransport
    {
        public bool SupportsStatusProbe => false;

        public Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<PrinterStatus> ProbeStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new PrinterStatus(false, false, false, false, true));
        }
    }

    private sealed class NullErpClient : IErpClient
    {
        public Task<ErpResult> PostEventAsync(string payloadJson, CancellationToken cancellationToken)
        {
            return Task.FromResult(ErpResult.Ok);
        }
    }
}
