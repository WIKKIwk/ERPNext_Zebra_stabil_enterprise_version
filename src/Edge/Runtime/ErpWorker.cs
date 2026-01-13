using ZebraBridge.Edge.Outbox;

namespace ZebraBridge.Edge.Runtime;

public sealed class ErpWorker
{
    private readonly ErpOutboxStore _erpOutbox;
    private readonly PrintOutboxStore _printOutbox;
    private readonly IErpClient _client;
    private readonly SemaphoreSlim _signal;
    private const long WaitPrintBaseMs = 2000;
    private const long WaitPrintMaxMs = 30000;
    private const long MaxWaitPrintAgeMs = 30 * 60 * 1000;

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
                if (nowMs - job.CreatedAtMs >= MaxWaitPrintAgeMs)
                {
                    await _erpOutbox.MarkNeedsOperatorAsync(job.EventId, "WAIT_PRINT_TIMEOUT", NowMs());
                    continue;
                }

                var backoff = WaitPrintBackoffMs(job.WaitPrintChecks);
                var nextRetry = NowMs() + backoff;
                await _erpOutbox.MarkWaitPrintAsync(job.EventId, nextRetry, NowMs());
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

    private static long WaitPrintBackoffMs(int waitPrintChecks)
    {
        var backoff = (long)(WaitPrintBaseMs * Math.Pow(2, Math.Max(0, waitPrintChecks)));
        return Math.Min(WaitPrintMaxMs, backoff);
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
