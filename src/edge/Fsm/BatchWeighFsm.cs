using System.Security.Cryptography;
using System.Text;

namespace ZebraBridge.Edge;

public sealed class BatchWeighFsm
{
    private readonly FsmConfig _config;
    private readonly StabilityDetector _detector;
    private readonly OutboxStore _outbox;
    private readonly StabilitySettings _settings;

    private double _stateEnteredAt;
    private double? _belowEmptySince;
    private double? _printSentAt;
    private double? _receivedAt;
    private double _nextSendAllowedAt;

    private OutboxJob? _currentJob;
    private double _lockWeight;
    private bool _printSent;

    public BatchWeighFsm(FsmConfig config, StabilityDetector detector, OutboxStore outbox, StabilitySettings settings)
    {
        _config = config;
        _detector = detector;
        _outbox = outbox;
        _settings = settings;
        State = FsmState.WaitEmpty;
    }

    public FsmState State { get; private set; }
    public PauseReason PauseReason { get; private set; } = PauseReason.None;
    public string? ActiveBatchId { get; private set; }
    public string? ActiveProductId { get; private set; }
    public string? PendingProductId { get; private set; }
    public string? DeviceId { get; private set; }
    public bool PrinterReady { get; set; } = true;
    public CompletionMode CompletionMode { get; set; } = CompletionMode.StatusQuery;
    public double LastWeight { get; private set; }

    public event Action<PrintRequest>? PrintRequested;

    public void OnBatchStart(BatchConfig config, double nowSeconds)
    {
        DeviceId = config.DeviceId;
        ActiveBatchId = config.BatchId;
        ActiveProductId = config.ProductId;
        PendingProductId = null;
        _outbox.StartBatch(config.DeviceId, config.BatchId, config.ProductId, ToMs(nowSeconds));
        if (State == FsmState.Paused && PauseReason == PauseReason.BatchStop)
        {
            PauseReason = PauseReason.None;
            EnterState(FsmState.WaitEmpty, nowSeconds, reenter: true);
        }
    }

    public void OnBatchStop(double nowSeconds)
    {
        PauseReason = PauseReason.BatchStop;
        if (DeviceId != null && ActiveBatchId != null)
        {
            _outbox.StopBatch(DeviceId, ActiveBatchId, "BATCH_STOP", ToMs(nowSeconds));
        }
        EnterState(FsmState.Paused, nowSeconds);
    }

    public void OnProductSwitch(string productId)
    {
        PendingProductId = productId;
        if (State == FsmState.WaitEmpty)
        {
            EnterState(FsmState.WaitEmpty, _stateEnteredAt, reenter: true);
        }
    }

    public void OnOperatorResume(double nowSeconds)
    {
        if (State != FsmState.Paused)
        {
            return;
        }

        if (LastWeight < _settings.EmptyThreshold)
        {
            PauseReason = PauseReason.None;
            EnterState(FsmState.WaitEmpty, nowSeconds);
        }
    }

    public void OnWeightSample(WeightSample sample)
    {
        LastWeight = sample.Value;
        var now = sample.TimeSeconds;
        UpdateEmpty(sample.Value, now);
        _detector.AddSample(sample);

        switch (State)
        {
            case FsmState.WaitEmpty:
                if (sample.Value >= _settings.PlacementMinWeight)
                {
                    _detector.Reset();
                    _printSent = false;
                    EnterState(FsmState.Loading, now);
                }
                break;
            case FsmState.Loading:
                if (IsBelowEmptyForClear(now))
                {
                    _detector.Reset();
                    EnterState(FsmState.WaitEmpty, now);
                    break;
                }
                if (now - _stateEnteredAt >= _config.SettleSeconds && _detector.SampleCount >= _config.MinSamples)
                {
                    EnterState(FsmState.Settling, now);
                }
                break;
            case FsmState.Settling:
                if (IsBelowEmptyForClear(now))
                {
                    _detector.Reset();
                    EnterState(FsmState.WaitEmpty, now);
                    break;
                }
                if (_detector.IsStable)
                {
                    LockAndCreateEvent(now);
                }
                break;
            case FsmState.Locked:
                HandleLocked(now, sample.Value);
                break;
            case FsmState.Printing:
                HandlePrinting(now, sample.Value);
                break;
            case FsmState.PostGuard:
                if (IsBelowEmptyForClear(now))
                {
                    if (_currentJob != null)
                    {
                        _outbox.MarkDone(_currentJob.EventId, ToMs(now));
                    }
                    EnterState(FsmState.WaitEmpty, now);
                }
                break;
            case FsmState.Paused:
                break;
        }
    }

    public void OnPrinterReceived(string eventId, double nowSeconds)
    {
        if (_currentJob == null || _currentJob.EventId != eventId)
        {
            return;
        }

        _receivedAt = nowSeconds;
        _outbox.MarkReceived(eventId, ToMs(nowSeconds));
    }

    public void OnPrinterCompleted(string eventId, double nowSeconds)
    {
        if (_currentJob == null || _currentJob.EventId != eventId)
        {
            return;
        }

        _outbox.MarkCompleted(eventId, ToMs(nowSeconds));
        EnterState(FsmState.PostGuard, nowSeconds);
    }

    public void OnScanReconciled(string eventId, double nowSeconds)
    {
        if (CompletionMode != CompletionMode.ScanRecon)
        {
            return;
        }

        OnPrinterCompleted(eventId, nowSeconds);
    }

    private void HandleLocked(double now, double weight)
    {
        var changeLimit = _settings.ChangeLimit(_lockWeight);
        if (Math.Abs(weight - _lockWeight) > changeLimit)
        {
            if (_printSent)
            {
                PauseReason = PauseReason.ReweighRequired;
                EnterState(FsmState.Paused, now);
            }
            else
            {
                _detector.Reset();
                EnterState(FsmState.Settling, now);
            }
            return;
        }

        if (!_printSent)
        {
            if (!PrinterReady)
            {
                PauseReason = PauseReason.PrinterOffline;
                EnterState(FsmState.Paused, now);
                return;
            }

            SendPrint(now);
        }
    }

    private void HandlePrinting(double now, double weight)
    {
        var changeLimit = _settings.ChangeLimit(_lockWeight);
        if (Math.Abs(weight - _lockWeight) > changeLimit)
        {
            PauseReason = PauseReason.ReweighRequired;
            EnterState(FsmState.Paused, now);
            return;
        }

        if (_printSent && _receivedAt == null && _printSentAt.HasValue)
        {
            if (now - _printSentAt.Value >= _config.SendTimeoutSeconds)
            {
                var nextRetry = now + _config.RetryBackoffSeconds;
                if (_currentJob != null)
                {
                    _outbox.MarkRetry(_currentJob.EventId, ToMs(nextRetry), "SEND_TIMEOUT", ToMs(now));
                }
                _printSent = false;
                _nextSendAllowedAt = nextRetry;
            }
        }

        if (_receivedAt.HasValue && now - _receivedAt.Value >= _config.CompletedTimeoutSeconds)
        {
            PauseReason = PauseReason.PrintTimeout;
            EnterState(FsmState.Paused, now);
            return;
        }

        if (!_printSent && now >= _nextSendAllowedAt && PrinterReady)
        {
            SendPrint(now);
        }
    }

    private void LockAndCreateEvent(double now)
    {
        if (DeviceId == null || ActiveBatchId == null)
        {
            return;
        }

        _lockWeight = _detector.Mean;
        var payload = BuildPayload(_lockWeight, now);
        var payloadHash = ComputeHash(payload);
        var job = _outbox.CreateEventAndEnqueue(
            DeviceId,
            ActiveBatchId,
            payload,
            payloadHash,
            CompletionMode,
            ToMs(now));
        _currentJob = job;
        _printSent = false;
        _receivedAt = null;
        _printSentAt = null;
        _nextSendAllowedAt = now;
        EnterState(FsmState.Locked, now);
    }

    private void SendPrint(double now)
    {
        if (_currentJob == null)
        {
            return;
        }

        _printSent = true;
        _printSentAt = now;
        var request = new PrintRequest(
            _currentJob.EventId,
            _currentJob.BatchId,
            _currentJob.Sequence,
            _currentJob.PayloadJson,
            CompletionMode);
        PrintRequested?.Invoke(request);
        EnterState(FsmState.Printing, now);
    }

    private void EnterState(FsmState next, double now, bool reenter = false)
    {
        if (!reenter && State == next)
        {
            return;
        }

        State = next;
        _stateEnteredAt = now;

        switch (next)
        {
            case FsmState.WaitEmpty:
                _lockWeight = 0;
                _currentJob = null;
                _printSent = false;
                _receivedAt = null;
                _printSentAt = null;
                ApplyPendingProduct();
                break;
            case FsmState.Loading:
            case FsmState.Settling:
            case FsmState.Locked:
                _printSent = false;
                break;
            case FsmState.Printing:
                break;
            case FsmState.PostGuard:
                break;
            case FsmState.Paused:
                break;
        }
    }

    private void ApplyPendingProduct()
    {
        if (PendingProductId == null)
        {
            return;
        }

        ActiveProductId = PendingProductId;
        PendingProductId = null;
    }

    private void UpdateEmpty(double weight, double now)
    {
        if (weight < _settings.EmptyThreshold)
        {
            _belowEmptySince ??= now;
        }
        else
        {
            _belowEmptySince = null;
        }
    }

    private bool IsBelowEmptyForClear(double now)
    {
        return _belowEmptySince.HasValue && (now - _belowEmptySince.Value) >= _config.ClearSeconds;
    }

    private static long ToMs(double seconds)
    {
        return (long)Math.Round(seconds * 1000);
    }

    private string BuildPayload(double weight, double nowSeconds)
    {
        var productId = ActiveProductId ?? string.Empty;
        var batchId = ActiveBatchId ?? string.Empty;
        return $"{{\"event_weight\":{weight:F3},\"product_id\":\"{productId}\",\"batch_id\":\"{batchId}\",\"ts\":{nowSeconds:F3}}}";
    }

    private static string ComputeHash(string payload)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
