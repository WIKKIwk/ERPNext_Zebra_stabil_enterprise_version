namespace ZebraBridge.Edge;

public sealed class SerialScaleAdapter : IScaleAdapter
{
    public IAsyncEnumerable<WeightSample> StreamAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Serial adapter is not implemented in this prototype.");
    }
}
