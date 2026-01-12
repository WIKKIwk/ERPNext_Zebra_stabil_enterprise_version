namespace ZebraBridge.Edge.Stability;

public sealed class StabilitySettings
{
    public StabilitySettings(
        double sigma,
        double res,
        double windowSeconds,
        double eps,
        double epsAlign,
        double emptyThreshold,
        double placementMinWeight,
        double slopeLimit)
    {
        Sigma = sigma;
        Res = res;
        WindowSeconds = windowSeconds;
        Eps = eps;
        EpsAlign = epsAlign;
        EmptyThreshold = emptyThreshold;
        PlacementMinWeight = placementMinWeight;
        SlopeLimit = slopeLimit;
    }

    public double Sigma { get; }
    public double Res { get; }
    public double WindowSeconds { get; }
    public double Eps { get; }
    public double EpsAlign { get; }
    public double EmptyThreshold { get; }
    public double PlacementMinWeight { get; set; }
    public double SlopeLimit { get; }

    public double ChangeLimit(double lockWeight)
    {
        return Math.Max(4 * Sigma, Math.Max(0.005 * lockWeight, 2 * Res));
    }
}
