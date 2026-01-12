using ZebraBridge.Edge.Adapters;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Outbox;

namespace ZebraBridge.Edge.Runtime;

public sealed class PrintWorker
{
    private readonly PrintOutboxStore _printOutbox;
    private readonly IPrinterTransport _transport;
    private readonly Action<FsmEvent> _controlEnqueue;
    private readonly SemaphoreSlim _signal;

    public PrintWorker(
        PrintOutboxStore printOutbox,
        IPrinterTransport transport,
        Action<FsmEvent> controlEnqueue,
        SemaphoreSlim signal)
    {
        _printOutbox = printOutbox;
        _transport = transport;
        _controlEnqueue = controlEnqueue;
        _signal = signal;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
            var nowMs = NowMs();
            var job = await _printOutbox.FetchNextAsync(nowMs);
            if (job == null)
            {
                continue;
            }

            try
            {
                await _transport.SendAsync(job.PayloadJson, cancellationToken);
                await _printOutbox.MarkStatusAsync(job.EventId, PrintJobStatus.Sent, NowMs());
            }
            catch (Exception ex)
            {
                var nextRetry = NowMs() + BackoffMs(job.Attempts + 1);
                await _printOutbox.MarkRetryAsync(job.EventId, nextRetry, ex.Message, NowMs());
                continue;
            }

            if (!_transport.SupportsStatusProbe)
            {
                await _printOutbox.UpdateCompletionModeAsync(job.EventId, "SCAN_RECON", NowMs());
                continue;
            }

            var received = await ProbeReceivedAsync(job.EventId, cancellationToken);
            if (!received)
            {
                var nextRetry = NowMs() + BackoffMs(job.Attempts + 1);
                await _printOutbox.MarkRetryAsync(job.EventId, nextRetry, "SEND_TIMEOUT", NowMs());
                continue;
            }

            _controlEnqueue(new PrinterReceivedEvent(job.EventId, NowSeconds()));
            await _printOutbox.MarkStatusAsync(job.EventId, PrintJobStatus.Received, NowMs());

            var completed = await ProbeCompletedAsync(job.EventId, cancellationToken);
            if (completed == CompletionOutcome.Completed)
            {
                _controlEnqueue(new PrinterCompletedEvent(job.EventId, NowSeconds()));
                await _printOutbox.MarkStatusAsync(job.EventId, PrintJobStatus.Completed, NowMs());
                await _printOutbox.MarkStatusAsync(job.EventId, PrintJobStatus.Done, NowMs());
                continue;
            }

            if (completed == CompletionOutcome.Timeout)
            {
                _controlEnqueue(new PauseEvent(PauseReason.PrintTimeout, NowSeconds()));
            }
        }
    }

    private async Task<bool> ProbeReceivedAsync(string eventId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = await _transport.ProbeStatusAsync(cancellationToken);
            if (status.Ready && !status.Busy)
            {
                return true;
            }
            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    private async Task<CompletionOutcome> ProbeCompletedAsync(string eventId, CancellationToken cancellationToken)
    {
        var start = NowMs();
        while (NowMs() - start <= 5000)
        {
            var status = await _transport.ProbeStatusAsync(cancellationToken);
            if (status.Busy)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (status.Ready && status.JobBufferEmpty)
            {
                if (status.RfidOk)
                {
                    return CompletionOutcome.Completed;
                }

                if (status.RfidUnknown)
                {
                    await _printOutbox.UpdateCompletionModeAsync(eventId, "SCAN_RECON", NowMs());
                    return CompletionOutcome.ScanReconFallback;
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        return CompletionOutcome.Timeout;
    }

    private static long NowMs()
    {
        return (long)Math.Round(NowSeconds() * 1000);
    }

    private static double NowSeconds()
    {
        return (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
    }

    private static long BackoffMs(int attempts)
    {
        var backoff = Math.Min(60000, (int)Math.Pow(2, attempts - 1) * 1000);
        return backoff;
    }
}

public enum CompletionOutcome
{
    Completed,
    ScanReconFallback,
    Timeout
}
