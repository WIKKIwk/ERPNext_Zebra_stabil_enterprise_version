namespace ZebraBridge.Edge;

public sealed record PrintRequest(
    string EventId,
    string BatchId,
    int Sequence,
    string PayloadJson,
    CompletionMode CompletionMode);
