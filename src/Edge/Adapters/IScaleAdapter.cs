namespace ZebraBridge.Edge.Adapters;

public interface IScaleAdapter
{
    IAsyncEnumerable<WeightSample> StreamAsync(CancellationToken cancellationToken);
}
