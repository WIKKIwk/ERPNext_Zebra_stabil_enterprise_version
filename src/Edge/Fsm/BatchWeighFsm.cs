using ZebraBridge.Edge.Stability;

namespace ZebraBridge.Edge.Fsm;

public sealed class BatchWeighFsm
{
    private readonly FsmConfig _config;
    private readonly StabilityDetector _detector;
    private readonly StabilitySettings _settings;
    private readonly List<FsmAction> _actions = new();

    private double _stateEnteredAt;
    private double? _belowEmptySince;
    private bool _printRequested;
    private bool _printSent;
    private string? _currentEventId;
    private double _lockWeight;
    private double? _printSentAt;
    private double? _receivedAt;

    public BatchWeighFsm(FsmConfig config, StabilityDetector detector, StabilitySettings settings)
    {
        _config = config;
        _detector = detector;
        _settings = settings;
        State = FsmState.WaitEmpty;
        PauseReason = PauseReason.None;
    }

    public FsmState State { get; private set; }
    public PauseReason PauseReason { get; private set; }
    public string? ActiveBatchId { get; private set; }
    public string? ActiveProductId { get; private set; }
    public string? PendingProductId { get; private set; }
    public string? DeviceId { get; private set; }

    public IReadOnlyList<FsmAction> Handle(FsmEvent ev)
    {
        _actions.Clear();

        switch (ev)
        {
            case BatchStartEvent batchStart:
                OnBatchStart(batchStart);
                break;
            case BatchStopEvent batchStop:
                OnBatchStop(batchStop);
                break;
            case ProductSwitchEvent productSwitch:
                OnProductSwitch(productSwitch);
                break;
            case PrintEnqueuedEvent printEnqueued:
                OnPrintEnqueued(printEnqueued);
                break;
            case PrinterReceivedEvent received:
                OnPrinterReceived(received);
                break;
            case PrinterCompletedEvent completed:
                OnPrinterCompleted(completed);
                break;
            case ScanReconEvent scanRecon:
                OnScanRecon(scanRecon);
                break;
            case PauseEvent pause:
                OnPause(pause);
                break;
            case ReasonClearedEvent cleared:
                OnReasonCleared(cleared);
                break;
            case SampleEvent sample:
                OnSample(sample);
                break;
        }

        return _actions.ToArray();
    }

    private void OnBatchStart(BatchStartEvent ev)
    {
        DeviceId = ev.DeviceId;
        ActiveBatchId = ev.BatchId;
        ActiveProductId = ev.ProductId;
        PendingProductId = null;
        _settings.PlacementMinWeight = ev.PlacementMinWeight;
        _detector.Reset();
        _printRequested = false;
        _printSent = false;
        _currentEventId = null;

        if (State == FsmState.Paused && PauseReason == PauseReason.BatchStop)
        {
            PauseReason = PauseReason.None;
            EnterState(FsmState.WaitEmpty, ev.TimestampSeconds, reenter: true);
        }
    }

    private void OnBatchStop(BatchStopEvent ev)
    {
        PauseReason = PauseReason.BatchStop;
        EnterState(FsmState.Paused, ev.TimestampSeconds);
    }

    private void OnProductSwitch(ProductSwitchEvent ev)
    {
        PendingProductId = ev.ProductId;
        if (State == FsmState.WaitEmpty)
        {
            ApplyPendingProduct();
        }
    }

    private void OnPrintEnqueued(PrintEnqueuedEvent ev)
    {
        if (_currentEventId == null || _currentEventId != ev.EventId)
        {
            return;
        }

        _printSent = true;
        _printRequested = false;
        _printSentAt = ev.TimestampSeconds;
        EnterState(FsmState.Printing, ev.TimestampSeconds);
    }

    private void OnPrinterReceived(PrinterReceivedEvent ev)
    {
        if (_currentEventId == null || _currentEventId != ev.EventId)
        {
            return;
        }

        _receivedAt = ev.TimestampSeconds;
    }

    private void OnPrinterCompleted(PrinterCompletedEvent ev)
    {
        if (_currentEventId == null || _currentEventId != ev.EventId)
        {
            return;
        }

        EnterState(FsmState.PostGuard, ev.TimestampSeconds);
    }

    private void OnScanRecon(ScanReconEvent ev)
    {
        if (_currentEventId == null || _currentEventId != ev.EventId)
        {
            return;
        }

        EnterState(FsmState.PostGuard, ev.TimestampSeconds);
    }

    private void OnPause(PauseEvent ev)
    {
        PauseReason = ev.Reason;
        EnterState(FsmState.Paused, ev.TimestampSeconds);
    }

    private void OnReasonCleared(ReasonClearedEvent ev)
    {
        if (State != FsmState.Paused || PauseReason != ev.Reason)
        {
            return;
        }

        PauseReason = PauseReason.None;
        EnterState(FsmState.WaitEmpty, ev.TimestampSeconds);
    }

    private void OnSample(SampleEvent ev)
    {
        var sample = ev.Sample;
        UpdateEmpty(sample.Value, sample.MonoTimeSeconds);

        if (State == FsmState.Paused)
        {
            if (PauseReason == PauseReason.ReweighRequired || PauseReason == PauseReason.BatchStop)
            {
                if (IsBelowEmptyForClear(sample.MonoTimeSeconds))
                {
                    PauseReason = PauseReason.None;
                    EnterState(FsmState.WaitEmpty, sample.MonoTimeSeconds);
                }
            }
            return;
        }

        _detector.AddSample(sample);

        switch (State)
        {
            case FsmState.WaitEmpty:
                if (sample.Value >= _settings.PlacementMinWeight)
                {
                    _detector.Reset();
                    _printRequested = false;
                    _printSent = false;
                    _currentEventId = null;
                    EnterState(FsmState.Loading, sample.MonoTimeSeconds);
                }
                break;
            case FsmState.Loading:
                if (IsBelowEmptyForClear(sample.MonoTimeSeconds))
                {
                    _detector.Reset();
                    EnterState(FsmState.WaitEmpty, sample.MonoTimeSeconds);
                    break;
                }
                if (sample.MonoTimeSeconds - _stateEnteredAt >= _config.SettleSeconds
                    && _detector.SampleCount >= _config.MinSamples)
                {
                    EnterState(FsmState.Settling, sample.MonoTimeSeconds);
                }
                break;
            case FsmState.Settling:
                if (IsBelowEmptyForClear(sample.MonoTimeSeconds))
                {
                    _detector.Reset();
                    EnterState(FsmState.WaitEmpty, sample.MonoTimeSeconds);
                    break;
                }
                if (_detector.IsStable)
                {
                    LockAndRequestPrint(sample.MonoTimeSeconds);
                }
                break;
            case FsmState.Locked:
                HandleLocked(sample.MonoTimeSeconds, sample.Value);
                break;
            case FsmState.Printing:
                HandlePrinting(sample.MonoTimeSeconds, sample.Value);
                break;
            case FsmState.PostGuard:
                if (IsBelowEmptyForClear(sample.MonoTimeSeconds))
                {
                    EnterState(FsmState.WaitEmpty, sample.MonoTimeSeconds);
                }
                break;
        }
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
                _printRequested = false;
                EnterState(FsmState.Settling, now);
            }
            return;
        }

        if (!_printRequested && !_printSent)
        {
            _printRequested = true;
            EmitPrintRequested(now);
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
    }

    private void LockAndRequestPrint(double now)
    {
        if (ActiveBatchId == null || ActiveProductId == null || DeviceId == null)
        {
            return;
        }

        _lockWeight = _detector.Mean;
        _currentEventId = Guid.NewGuid().ToString("N");
        _printSent = false;
        _printRequested = true;
        _printSentAt = null;
        _receivedAt = null;
        EnterState(FsmState.Locked, now);
        EmitPrintRequested(now);
    }

    private void EmitPrintRequested(double now)
    {
        if (_currentEventId == null || ActiveBatchId == null || ActiveProductId == null || DeviceId == null)
        {
            return;
        }

        _actions.Add(new PrintRequestedAction(
            _currentEventId,
            DeviceId,
            ActiveBatchId,
            ActiveProductId,
            _lockWeight,
            now));
    }

    private void EnterState(FsmState state, double now, bool reenter = false)
    {
        if (!reenter && State == state)
        {
            return;
        }

        State = state;
        _stateEnteredAt = now;

        if (State == FsmState.WaitEmpty)
        {
            _lockWeight = 0;
            _currentEventId = null;
            _printRequested = false;
            _printSent = false;
            _printSentAt = null;
            _receivedAt = null;
            ApplyPendingProduct();
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

}
