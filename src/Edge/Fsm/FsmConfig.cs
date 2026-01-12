namespace ZebraBridge.Edge.Fsm;

public sealed record FsmConfig(
    double SettleSeconds,
    double ClearSeconds,
    int MinSamples)
{
    public static FsmConfig Default => new(
        SettleSeconds: 0.50,
        ClearSeconds: 0.70,
        MinSamples: 10);
}
