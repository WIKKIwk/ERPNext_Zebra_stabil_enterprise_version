namespace ZebraBridge.Edge;

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
    ReweighRequired
}

public enum CompletionMode
{
    StatusQuery,
    ScanRecon
}

public enum PrinterAck
{
    Received,
    Completed
}
