namespace ZebraBridge.Edge.Fsm;

public abstract record FsmAction;

public sealed record PrintRequestedAction(
    string EventId,
    string DeviceId,
    string BatchId,
    string ProductId,
    double Weight,
    double TimestampSeconds) : FsmAction;

public sealed record PrintCompletedAction(string EventId, double TimestampSeconds) : FsmAction;

public sealed record PauseAction(PauseReason Reason, double TimestampSeconds) : FsmAction;
