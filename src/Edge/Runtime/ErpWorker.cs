using ZebraBridge.Edge.Outbox;

namespace ZebraBridge.Edge.Runtime;

public sealed class ErpWorker
{
    private readonly ErpOutboxStore _erpOutbox;
    private readonly PrintOutboxStore _printOutbox;
    private readonly IErpClient _client;
    private readonly SemaphoreSlim _signal;

    public ErpWorker(ErpOutboxStore erpOutbox, PrintOutboxStore printOutbox, IErpClient client, SemaphoreSlim signal)
    {
        _erpOutbox = erpOutbox;
        _printOutbox = printOutbox;
        _client = client;
        _signal = signal;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
            var nowMs = NowMs();
            var job = await _erpOutbox.FetchNextAsync(nowMs);
            if (job == null)
            {
                continue;
            }

            var printStatus = await _printOutbox.GetStatusAsync(job.EventId);
            if (!IsPrintCompleted(printStatus))
            {
                var nextRetry = NowMs() + 500;
                await _erpOutbox.MarkRetryAsync(job.EventId, nextRetry, "WAIT_PRINT", NowMs());
                continue;
            }

            var result = await _client.PostEventAsync(job.PayloadJson, cancellationToken);
            if (result == ErpResult.Ok || result == ErpResult.Conflict)
            {
                await _erpOutbox.MarkStatusAsync(job.EventId, ErpJobStatus.Done, NowMs());
                continue;
            }

            var nextRetry = NowMs() + BackoffMs(job.Attempts + 1);
            await _erpOutbox.MarkRetryAsync(job.EventId, nextRetry, result.ToString(), NowMs());
        }
    }

    private static bool IsPrintCompleted(string? status)
    {
        return status == PrintJobStatus.Completed || status == PrintJobStatus.Done;
    }

    private static long NowMs()
    {
        return (long)Math.Round((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency * 1000);
    }

    private static long BackoffMs(int attempts)
    {
        var backoff = Math.Min(60000, (int)Math.Pow(2, attempts - 1) * 1000);
        return backoff;
    }
}

public interface IErpClient
{
    Task<ErpResult> PostEventAsync(string payloadJson, CancellationToken cancellationToken);
}

public enum ErpResult
{
    Ok,
    Conflict,
    Retryable,
    Failed
}
