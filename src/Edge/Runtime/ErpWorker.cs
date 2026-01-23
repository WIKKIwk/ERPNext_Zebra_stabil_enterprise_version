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
    private const long RetryBaseMs = 1000;
    private const long RetryMaxMs = 60000;
    private const int RetryMaxExponent = 6;

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
            ErpJob? job;
            try
            {
                job = await _erpOutbox.FetchNextAsync(nowMs);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(500, cancellationToken);
                continue;
            }
            if (job == null)
            {
                continue;
            }

            string? printStatus;
            try
            {
                printStatus = await _printOutbox.GetStatusAsync(job.EventId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var nextRetry = NowMs() + BackoffMs(job.Attempts + 1);
                await _erpOutbox.MarkRetryAsync(job.EventId, nextRetry, ex.Message, NowMs());
                continue;
            }
            if (!IsPrintCompleted(printStatus))
            {
                var createdAtMs = job.CreatedAtMs <= 0 ? nowMs : job.CreatedAtMs;
                if (nowMs - createdAtMs >= MaxWaitPrintAgeMs)
                {
                    await _erpOutbox.MarkNeedsOperatorAsync(job.EventId, "WAIT_PRINT_TIMEOUT", NowMs());
                    continue;
                }

                var backoff = WaitPrintBackoffMs(job.WaitPrintChecks);
                var waitRetryAt = NowMs() + backoff;
                await _erpOutbox.MarkWaitPrintAsync(job.EventId, waitRetryAt, NowMs());
                continue;
            }

            ErpResult result;
            try
            {
                result = await _client.PostEventAsync(job.PayloadJson, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var nextRetry = NowMs() + BackoffMs(job.Attempts + 1);
                await _erpOutbox.MarkRetryAsync(job.EventId, nextRetry, ex.Message, NowMs());
                continue;
            }
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
        if (waitPrintChecks < 0)
        {
            waitPrintChecks = 0;
        }

        var exponent = Math.Min(waitPrintChecks, 4);
        var backoff = WaitPrintBaseMs * (1L << exponent);
        return Math.Min(WaitPrintMaxMs, backoff);
    }

    private static long NowMs()
    {
        return (long)Math.Round((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency * 1000);
    }

    private static long BackoffMs(int attempts)
    {
        if (attempts <= 0)
        {
            attempts = 1;
        }

        var exponent = Math.Clamp(attempts - 1, 0, RetryMaxExponent);
        var backoff = RetryBaseMs * (1L << exponent);
        backoff = Math.Min(RetryMaxMs, backoff);

        var jitterMax = (int)Math.Min(250, backoff / 10 + 1);
        var jitter = Random.Shared.Next(0, jitterMax);

        return Math.Min(RetryMaxMs, backoff + jitter);
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
