namespace ZebraBridge.Edge;

public static class OutboxStatus
{
    public const string New = "NEW";
    public const string Sent = "SENT";
    public const string Received = "RECEIVED";
    public const string Completed = "COMPLETED";
    public const string Done = "DONE";
    public const string Retry = "RETRY";
    public const string Fail = "FAIL";
}

public sealed record OutboxJob(
    string JobId,
    string EventId,
    string DeviceId,
    string BatchId,
    int Sequence,
    string Status,
    string PayloadJson,
    string PayloadHash,
    CompletionMode CompletionMode);
