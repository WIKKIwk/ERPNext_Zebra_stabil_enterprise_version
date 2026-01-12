using System.Threading.Channels;
using ZebraBridge.Edge;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Runtime;
using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task SampleSignalCoalescingLimitsWakeupsDeterministic()
    {
        var fake = new FakeFsm();
        var actionChannel = Channel.CreateUnbounded<FsmAction>();
        var signal = new SemaphoreSlim(0);
        var loop = new FsmEventLoop(fake, actionChannel.Writer, 4096, signal, new NullAlarmSink());

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var now = 0.0;
        for (var i = 0; i < 10_000; i++)
        {
            now += 0.001;
            loop.UpdateLatestSample(new WeightSample(1.0, "kg", now));
        }

        var waited = 0;
        while (fake.DispatchCount == 0 && waited < 1000)
        {
            await Task.Delay(1);
            waited++;
        }

        Assert.True(fake.DispatchCount > 0);
        Assert.True(loop.SampleWakeups <= fake.DispatchCount + 5);

        cts.Cancel();
        signal.Release();
        await runTask;
    }

    private sealed class FakeFsm : IFsmHandler
    {
        public int DispatchCount => _dispatchCount;
        private int _dispatchCount;

        public IReadOnlyList<FsmAction> Handle(FsmEvent ev)
        {
            if (ev is SampleEvent)
            {
                Interlocked.Increment(ref _dispatchCount);
            }

            return Array.Empty<FsmAction>();
        }
    }
}
