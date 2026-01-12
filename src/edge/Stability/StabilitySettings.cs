namespace ZebraBridge.Edge;

public sealed record StabilitySettings(
    double MedianDt,
    double Sigma,
    double Res,
    double Eps,
    double EpsAlign,
    double WindowSeconds,
    double EmptyThreshold,
    double PlacementMinWeight,
    double SlopeLimit)
{
    public double ChangeLimit(double lockWeight)
    {
        return Math.Max(4 * Sigma, Math.Max(0.005 * lockWeight, 2 * Res));
    }
}
