using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed class ScaleState : IScaleState
{
    private readonly object _lock = new();
    private ScaleReading _latest = new(
        Ok: false,
        Weight: null,
        Unit: "kg",
        Stable: null,
        TimestampMs: 0,
        Port: string.Empty,
        Raw: string.Empty,
        Error: "Scale reader not started"
    );

    public ScaleReading Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    public void Update(ScaleReading reading)
    {
        lock (_lock)
        {
            _latest = reading;
        }
    }
}
