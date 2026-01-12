namespace ZebraBridge.Edge;

public sealed class HidScaleAdapter : IScaleAdapter
{
    public IAsyncEnumerable<WeightSample> StreamAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("HID adapter is not implemented in this prototype.");
    }
}
