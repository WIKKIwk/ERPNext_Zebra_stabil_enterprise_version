using ZebraBridge.Edge;

namespace ZebraBridge.Edge.Fsm;

public abstract record FsmEvent;

public sealed record SampleEvent(WeightSample Sample) : FsmEvent;

public sealed record BatchStartEvent(
    string DeviceId,
    string BatchId,
    string ProductId,
    double PlacementMinWeight,
    double TimestampSeconds) : FsmEvent;

public sealed record BatchStopEvent(double TimestampSeconds) : FsmEvent;

public sealed record ProductSwitchEvent(string ProductId, double TimestampSeconds) : FsmEvent;

public sealed record PrintEnqueuedEvent(string EventId, double TimestampSeconds) : FsmEvent;

public sealed record PrinterReceivedEvent(string EventId, double TimestampSeconds) : FsmEvent;

public sealed record PrinterCompletedEvent(string EventId, double TimestampSeconds) : FsmEvent;

public sealed record ScanReconEvent(string EventId, double TimestampSeconds) : FsmEvent;

public sealed record PauseEvent(PauseReason Reason, double TimestampSeconds) : FsmEvent;

public sealed record ReasonClearedEvent(PauseReason Reason, double TimestampSeconds) : FsmEvent;
