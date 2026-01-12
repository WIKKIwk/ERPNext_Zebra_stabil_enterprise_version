namespace ZebraBridge.Edge.Fsm;

public enum FsmState
{
    WaitEmpty,
    Loading,
    Settling,
    Locked,
    Printing,
    PostGuard,
    Paused
}

public enum PauseReason
{
    None,
    BatchStop,
    PrinterOffline,
    PrintTimeout,
    ReweighRequired,
    ErpBackpressure,
    ControlQueueOverflow,
    DbError
}
