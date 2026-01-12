using ZebraBridge.Edge;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Stability;
using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class FsmTests
{
    [Fact]
    public void OnePlacementProducesSinglePrintRequest()
    {
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, settings);

        fsm.Handle(new BatchStartEvent("dev-1", "batch-1", "prod-1", 1.0, 0));

        var actions = new List<FsmAction>();
        var now = 0.0;

        for (var i = 0; i < 20; i++)
        {
            now += 0.1;
            actions.AddRange(fsm.Handle(new SampleEvent(new WeightSample(0.0, "kg", now))));
        }

        for (var i = 0; i < 30; i++)
        {
            now += 0.1;
            actions.AddRange(fsm.Handle(new SampleEvent(new WeightSample(5.0, "kg", now))));
        }

        var printCount = actions.OfType<PrintRequestedAction>().Count();
        Assert.Equal(1, printCount);
    }

    [Fact]
    public void ProductSwitchAppliesOnlyInWaitEmpty()
    {
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, settings);

        fsm.Handle(new BatchStartEvent("dev-1", "batch-1", "prod-A", 1.0, 0));

        var now = 0.0;
        for (var i = 0; i < 5; i++)
        {
            now += 0.1;
            fsm.Handle(new SampleEvent(new WeightSample(2.0, "kg", now)));
        }

        fsm.Handle(new ProductSwitchEvent("prod-B", now));
        Assert.Equal("prod-A", fsm.ActiveProductId);

        for (var i = 0; i < 10; i++)
        {
            now += 0.1;
            fsm.Handle(new SampleEvent(new WeightSample(0.0, "kg", now)));
        }

        Assert.Equal(FsmState.WaitEmpty, fsm.State);
        Assert.Equal("prod-B", fsm.ActiveProductId);
    }

    [Fact]
    public void WeightChangeAfterPrintPausesReweighRequired()
    {
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, settings);

        fsm.Handle(new BatchStartEvent("dev-1", "batch-1", "prod-1", 1.0, 0));

        var now = 0.0;
        for (var i = 0; i < 30; i++)
        {
            now += 0.1;
            var actions = fsm.Handle(new SampleEvent(new WeightSample(5.0, "kg", now)));
            var print = actions.OfType<PrintRequestedAction>().FirstOrDefault();
            if (print != null)
            {
                fsm.Handle(new PrintEnqueuedEvent(print.EventId, now));
                break;
            }
        }

        now += 0.1;
        fsm.Handle(new SampleEvent(new WeightSample(8.0, "kg", now)));

        Assert.Equal(FsmState.Paused, fsm.State);
        Assert.Equal(PauseReason.ReweighRequired, fsm.PauseReason);
    }

    private static StabilitySettings CreateSettings()
    {
        return new StabilitySettings(
            sigma: 0.01,
            res: 0.01,
            windowSeconds: 0.5,
            eps: 0.03,
            epsAlign: 0.06,
            emptyThreshold: 0.05,
            placementMinWeight: 1.0,
            slopeLimit: 0.02);
    }
}
