namespace ZebraBridge.Edge;

public interface IScaleAdapter
{
    IAsyncEnumerable<WeightSample> StreamAsync(CancellationToken cancellationToken);
}
