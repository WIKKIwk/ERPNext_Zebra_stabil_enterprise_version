namespace ZebraBridge.Core;

public sealed record ScaleReading(
    bool Ok,
    double? Weight,
    string Unit,
    bool? Stable,
    long TimestampMs,
    string Port,
    string Raw,
    string Error
);
