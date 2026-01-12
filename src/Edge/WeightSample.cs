namespace ZebraBridge.Edge;

public sealed record WeightSample(
    double Value,
    string Unit,
    double MonoTimeSeconds,
    bool Valid = true);
