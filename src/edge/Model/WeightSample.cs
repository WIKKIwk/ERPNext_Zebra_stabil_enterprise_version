namespace ZebraBridge.Edge;

public sealed record WeightSample(
    double Value,
    string Unit,
    double TimeSeconds,
    bool Valid = true,
    bool Connected = true);
