using ZebraBridge.Edge.Adapters;

namespace ZebraBridge.Edge.Runtime;

public sealed class ScaleReadLoop
{
    private readonly IScaleAdapter _adapter;
    private readonly FsmEventLoop _eventLoop;
    private readonly Func<bool> _erpBackpressure;

    public ScaleReadLoop(IScaleAdapter adapter, FsmEventLoop eventLoop, Func<bool> erpBackpressure)
    {
        _adapter = adapter;
        _eventLoop = eventLoop;
        _erpBackpressure = erpBackpressure;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var sample in _adapter.StreamAsync(cancellationToken))
        {
            if (_erpBackpressure())
            {
                await Task.Delay(250, cancellationToken);
            }

            _eventLoop.UpdateLatestSample(sample);
        }
    }
}
