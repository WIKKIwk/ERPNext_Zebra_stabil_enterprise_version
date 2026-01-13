using Microsoft.Data.Sqlite;
using ZebraBridge.Edge.Adapters;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Outbox;
using ZebraBridge.Edge.Runtime;
using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class PrintCompletionTests
{
    [Fact]
    public async Task PausedPrinterDoesNotPostToErp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"edge-{Guid.NewGuid():N}.db");
        var printStore = new PrintOutboxStore(dbPath);
        var erpStore = new ErpOutboxStore(dbPath);
        printStore.Initialize();
        erpStore.Initialize();

        var eventId = Guid.NewGuid().ToString("N");
        await SeedJobsAsync(dbPath, printStore, erpStore, eventId);

        var controlEvents = new List<FsmEvent>();
        var printer = new FakePrinterTransport(new[]
        {
            Status(paused: true)
        });

        var printSignal = new SemaphoreSlim(0);
        var printWorker = new PrintWorker(printStore, printer, ev => controlEvents.Add(ev), printSignal);

        using var cts = new CancellationTokenSource();
        var printTask = printWorker.RunAsync(cts.Token);
        printSignal.Release();

        await WaitForConditionAsync(async () =>
        {
            var status = await printStore.GetStatusAsync(eventId);
            return status == PrintJobStatus.Sent;
        }, timeoutMs: 500);

        var erpClient = new FakeErpClient();
        var erpSignal = new SemaphoreSlim(0);
        var erpWorker = new ErpWorker(erpStore, printStore, erpClient, erpSignal);
        var erpTask = erpWorker.RunAsync(cts.Token);
        erpSignal.Release();

        await Task.Delay(100);

        cts.Cancel();
        await IgnoreCancellationAsync(printTask);
        await IgnoreCancellationAsync(erpTask);

        Assert.Contains(controlEvents, ev => ev is PauseEvent { Reason: PauseReason.PrinterPaused });
        Assert.Equal(0, erpClient.CallCount);
    }

    [Fact]
    public async Task CompletedPrintPostsToErpOnce()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"edge-{Guid.NewGuid():N}.db");
        var printStore = new PrintOutboxStore(dbPath);
        var erpStore = new ErpOutboxStore(dbPath);
        printStore.Initialize();
        erpStore.Initialize();

        var eventId = Guid.NewGuid().ToString("N");
        await SeedJobsAsync(dbPath, printStore, erpStore, eventId);

        var printer = new FakePrinterTransport(new[]
        {
            Status(ready: true),
            Status(ready: true, jobBufferEmpty: true, rfidOk: true)
        });

        var printSignal = new SemaphoreSlim(0);
        var printWorker = new PrintWorker(printStore, printer, _ => { }, printSignal);

        using var cts = new CancellationTokenSource();
        var printTask = printWorker.RunAsync(cts.Token);
        printSignal.Release();

        await WaitForConditionAsync(async () =>
        {
            var status = await printStore.GetStatusAsync(eventId);
            return status == PrintJobStatus.Done;
        }, timeoutMs: 1000);

        var erpClient = new FakeErpClient();
        var erpSignal = new SemaphoreSlim(0);
        var erpWorker = new ErpWorker(erpStore, printStore, erpClient, erpSignal);
        var erpTask = erpWorker.RunAsync(cts.Token);

        erpSignal.Release();
        await WaitForConditionAsync(() => Task.FromResult(erpClient.CallCount == 1), timeoutMs: 500);
        erpSignal.Release();
        await Task.Delay(50);

        cts.Cancel();
        await IgnoreCancellationAsync(printTask);
        await IgnoreCancellationAsync(erpTask);

        Assert.Equal(1, erpClient.CallCount);
    }

    [Fact]
    public async Task RfidUnknownTriggersScanRecon()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"edge-{Guid.NewGuid():N}.db");
        var printStore = new PrintOutboxStore(dbPath);
        var erpStore = new ErpOutboxStore(dbPath);
        printStore.Initialize();
        erpStore.Initialize();

        var eventId = Guid.NewGuid().ToString("N");
        await SeedJobsAsync(dbPath, printStore, erpStore, eventId);

        var controlEvents = new List<FsmEvent>();
        var printer = new FakePrinterTransport(new[]
        {
            Status(ready: true),
            Status(ready: true, jobBufferEmpty: true, rfidUnknown: true)
        });

        var printSignal = new SemaphoreSlim(0);
        var printWorker = new PrintWorker(printStore, printer, ev => controlEvents.Add(ev), printSignal);

        using var cts = new CancellationTokenSource();
        var printTask = printWorker.RunAsync(cts.Token);
        printSignal.Release();

        await WaitForConditionAsync(() => Task.FromResult(controlEvents.Any(ev => ev is ScanReconEvent)), timeoutMs: 500);

        cts.Cancel();
        await IgnoreCancellationAsync(printTask);

        var status = await printStore.GetStatusAsync(eventId);
        Assert.NotEqual(PrintJobStatus.Completed, status);
        Assert.NotEqual(PrintJobStatus.Done, status);
        Assert.Contains(controlEvents, ev => ev is ScanReconEvent);
        Assert.DoesNotContain(controlEvents, ev => ev is PrinterCompletedEvent);
    }

    private static async Task SeedJobsAsync(
        string dbPath,
        PrintOutboxStore printStore,
        ErpOutboxStore erpStore,
        string eventId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        var nowMs = 0L;
        var printJob = new PrintJob(
            Guid.NewGuid().ToString("N"),
            eventId,
            "dev-1",
            "batch-1",
            1,
            PrintJobStatus.New,
            "{}",
            "hash",
            "STATUS_QUERY",
            0,
            null);

        var erpJob = new ErpJob(
            Guid.NewGuid().ToString("N"),
            eventId,
            "dev-1",
            "batch-1",
            1,
            ErpJobStatus.New,
            "{}",
            "hash",
            0,
            nowMs,
            null);

        await printStore.TryInsertAsync(connection, null, printJob, nowMs);
        await erpStore.TryInsertAsync(connection, null, erpJob, nowMs);

        using var commit = connection.CreateCommand();
        commit.CommandText = "COMMIT;";
        commit.ExecuteNonQuery();
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

    private static PrinterStatus Status(
        bool ready = false,
        bool busy = false,
        bool jobBufferEmpty = false,
        bool rfidOk = false,
        bool rfidUnknown = false,
        bool paused = false,
        bool error = false,
        bool offline = false)
    {
        return new PrinterStatus(ready, busy, jobBufferEmpty, rfidOk, rfidUnknown, paused, error, offline);
    }

    private sealed class FakePrinterTransport : IPrinterTransport
    {
        private readonly Queue<PrinterStatus> _statuses;
        private PrinterStatus _last;

        public FakePrinterTransport(IEnumerable<PrinterStatus> statuses)
        {
            _statuses = new Queue<PrinterStatus>(statuses);
            _last = _statuses.Count > 0 ? _statuses.Peek() : Status();
        }

        public bool SupportsStatusProbe => true;

        public Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<PrinterStatus> ProbeStatusAsync(CancellationToken cancellationToken)
        {
            if (_statuses.Count > 0)
            {
                _last = _statuses.Dequeue();
            }
            return Task.FromResult(_last);
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
