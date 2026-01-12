namespace ZebraBridge.Edge;

public sealed record BatchConfig(
    string DeviceId,
    string BatchId,
    string ProductId,
    double PlacementMinWeight);

public sealed record FsmConfig(
    double SettleSeconds,
    double ClearSeconds,
    int MinSamples,
    double SendTimeoutSeconds,
    double CompletedTimeoutSeconds,
    double RetryBackoffSeconds)
{
    public static FsmConfig Default => new(
        SettleSeconds: 0.50,
        ClearSeconds: 0.70,
        MinSamples: 10,
        SendTimeoutSeconds: 1.50,
        CompletedTimeoutSeconds: 5.00,
        RetryBackoffSeconds: 1.00);
}
