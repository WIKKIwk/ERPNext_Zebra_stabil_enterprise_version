namespace ZebraBridge.Edge.Fsm;

public enum FsmState
{
    WaitEmpty,
    Loading,
    Settling,
    Locked,
    Printing,
    ScanReconRequired,
    PostGuard,
    Paused
}

public enum PauseReason
{
    None,
    BatchStop,
    PrinterOffline,
    PrinterPaused,
    PrinterError,
    PrintTimeout,
    ReweighRequired,
    ErpBackpressure,
    ControlQueueOverflow,
    DbError
}
