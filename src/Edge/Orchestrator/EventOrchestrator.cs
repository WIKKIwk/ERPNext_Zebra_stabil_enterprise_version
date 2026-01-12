using Microsoft.Data.Sqlite;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Outbox;

namespace ZebraBridge.Edge.Orchestrator;

public sealed class EventOrchestrator
{
    private readonly BatchStateStore _batchStateStore;
    private readonly PrintOutboxStore _printOutbox;
    private readonly ErpOutboxStore _erpOutbox;
    private readonly int _maxErpQueue;
    private readonly bool _supportsStatusProbe;
    private readonly Action<FsmEvent> _controlEnqueue;

    public EventOrchestrator(
        BatchStateStore batchStateStore,
        PrintOutboxStore printOutbox,
        ErpOutboxStore erpOutbox,
        int maxErpQueue,
        bool supportsStatusProbe,
        Action<FsmEvent> controlEnqueue)
    {
        _batchStateStore = batchStateStore;
        _printOutbox = printOutbox;
        _erpOutbox = erpOutbox;
        _maxErpQueue = maxErpQueue;
        _supportsStatusProbe = supportsStatusProbe;
        _controlEnqueue = controlEnqueue;
    }

    public void Initialize()
    {
        _batchStateStore.Initialize();
        _printOutbox.Initialize();
        _erpOutbox.Initialize();
    }

    public async Task HandleBatchStartAsync(BatchStartEvent ev)
    {
        _batchStateStore.StartBatch(ev.DeviceId, ev.BatchId, ev.ProductId, ToMs(ev.TimestampSeconds));
        await Task.CompletedTask;
    }

    public async Task HandleActionAsync(FsmAction action, CancellationToken cancellationToken)
    {
        if (action is not PrintRequestedAction print)
        {
            return;
        }

        var erpDepth = await _erpOutbox.CountPendingAsync();
        if (erpDepth >= _maxErpQueue)
        {
            _controlEnqueue(new PauseEvent(PauseReason.ErpBackpressure, print.TimestampSeconds));
            return;
        }

        using var connection = _batchStateStore.OpenConnection();
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        try
        {
            var nowMs = ToMs(print.TimestampSeconds);
            var seq = _batchStateStore.GetNextSequence(connection, null, print.DeviceId);
            _batchStateStore.IncrementSequence(connection, null, print.DeviceId, nowMs);

            var completionMode = _supportsStatusProbe ? "STATUS_QUERY" : "SCAN_RECON";
            var payloadJson = BuildPayload(print, seq);
            var payloadHash = ComputeHash(payloadJson);

            var printJob = new PrintJob(
                Guid.NewGuid().ToString("N"),
                print.EventId,
                print.DeviceId,
                print.BatchId,
                seq,
                PrintJobStatus.New,
                payloadJson,
                payloadHash,
                completionMode,
                0,
                null);

            var erpJob = new ErpJob(
                Guid.NewGuid().ToString("N"),
                print.EventId,
                print.DeviceId,
                print.BatchId,
                seq,
                ErpJobStatus.New,
                payloadJson,
                payloadHash,
                0,
                null);

            var printOk = await _printOutbox.TryInsertAsync(connection, null, printJob, nowMs);
            var erpOk = await _erpOutbox.TryInsertAsync(connection, null, erpJob, nowMs);

            if (!printOk || !erpOk)
            {
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
                _controlEnqueue(new PauseEvent(PauseReason.DbError, print.TimestampSeconds));
                return;
            }

            using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
            _controlEnqueue(new PrintEnqueuedEvent(print.EventId, print.TimestampSeconds));
        }
        catch
        {
            using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
            _controlEnqueue(new PauseEvent(PauseReason.DbError, print.TimestampSeconds));
        }

        await Task.CompletedTask;
    }

    private static long ToMs(double seconds)
    {
        return (long)Math.Round(seconds * 1000);
    }

    private static string BuildPayload(PrintRequestedAction print, int seq)
    {
        return $"{{\"event_id\":\"{print.EventId}\",\"device_id\":\"{print.DeviceId}\",\"batch_id\":\"{print.BatchId}\",\"product_id\":\"{print.ProductId}\",\"seq\":{seq},\"weight\":{print.Weight:F3},\"ts\":{print.TimestampSeconds:F3}}}";
    }

    private static string ComputeHash(string payload)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
