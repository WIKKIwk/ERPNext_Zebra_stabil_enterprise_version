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
            if (received.Outcome == ReceivedOutcome.Paused)
            {
                if (received.PauseReason is not null)
                {
                    _controlEnqueue(new PauseEvent(received.PauseReason.Value, NowSeconds()));
                }
                continue;
            }

            if (received.Outcome == ReceivedOutcome.Timeout)
            {
                var nextRetry = NowMs() + BackoffMs(job.Attempts + 1);
                await _printOutbox.MarkRetryAsync(job.EventId, nextRetry, "SEND_TIMEOUT", NowMs());
                continue;
            }

            _controlEnqueue(new PrinterReceivedEvent(job.EventId, NowSeconds()));
            await _printOutbox.MarkStatusAsync(job.EventId, PrintJobStatus.Received, NowMs());

            var completed = await ProbeCompletedAsync(job.EventId, cancellationToken);
            if (completed.Outcome == CompletionOutcome.Completed)
            {
                _controlEnqueue(new PrinterCompletedEvent(job.EventId, NowSeconds()));
                await _printOutbox.MarkStatusAsync(job.EventId, PrintJobStatus.Completed, NowMs());
                await _printOutbox.MarkStatusAsync(job.EventId, PrintJobStatus.Done, NowMs());
                continue;
            }

            if (completed.Outcome == CompletionOutcome.Timeout)
            {
                _controlEnqueue(new PauseEvent(PauseReason.PrintTimeout, NowSeconds()));
            }

            if (completed.Outcome == CompletionOutcome.ScanReconFallback)
            {
                _controlEnqueue(new ScanReconEvent(job.EventId, NowSeconds()));
            }

            if (completed.Outcome == CompletionOutcome.Paused && completed.PauseReason is not null)
            {
                _controlEnqueue(new PauseEvent(completed.PauseReason.Value, NowSeconds()));
            }
        }
    }

    private async Task<ReceivedProbeResult> ProbeReceivedAsync(string eventId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = await _transport.ProbeStatusAsync(cancellationToken);
            var pause = GetPauseReason(status);
            if (pause is not null)
            {
                return new ReceivedProbeResult(ReceivedOutcome.Paused, pause);
            }
            if (status.Ready && !status.Busy)
            {
                return new ReceivedProbeResult(ReceivedOutcome.Received, null);
            }
            await Task.Delay(200, cancellationToken);
        }

        return new ReceivedProbeResult(ReceivedOutcome.Timeout, null);
    }

    private async Task<CompletionProbeResult> ProbeCompletedAsync(string eventId, CancellationToken cancellationToken)
    {
        var start = NowMs();
        while (NowMs() - start <= 5000)
        {
            var status = await _transport.ProbeStatusAsync(cancellationToken);
            var pause = GetPauseReason(status);
            if (pause is not null)
            {
                return new CompletionProbeResult(CompletionOutcome.Paused, pause);
            }
            if (status.Busy)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (status.Ready && status.JobBufferEmpty)
            {
                if (status.RfidOk)
                {
                    return new CompletionProbeResult(CompletionOutcome.Completed, null);
                }

                if (status.RfidUnknown)
                {
                    await _printOutbox.UpdateCompletionModeAsync(eventId, "SCAN_RECON", NowMs());
                    return new CompletionProbeResult(CompletionOutcome.ScanReconFallback, null);
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        return new CompletionProbeResult(CompletionOutcome.Timeout, null);
    }

    private static PauseReason? GetPauseReason(PrinterStatus status)
    {
        if (status.Offline)
        {
            return PauseReason.PrinterOffline;
        }
        if (status.Error)
        {
            return PauseReason.PrinterError;
        }
        if (status.Paused)
        {
            return PauseReason.PrinterPaused;
        }
        return null;
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
    Timeout,
    Paused
}

public enum ReceivedOutcome
{
    Received,
    Timeout,
    Paused
}

public sealed record ReceivedProbeResult(ReceivedOutcome Outcome, PauseReason? PauseReason);
public sealed record CompletionProbeResult(CompletionOutcome Outcome, PauseReason? PauseReason);
