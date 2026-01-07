namespace ZebraBridge.Core;

public interface IScaleState
{
    ScaleReading Latest { get; }
    void Update(ScaleReading reading);
}
