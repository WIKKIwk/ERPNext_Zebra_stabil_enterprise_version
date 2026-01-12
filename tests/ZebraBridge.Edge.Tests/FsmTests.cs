using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class FsmTests
{
    [Fact]
    public void SinglePlacementProducesSinglePrintJob()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        var outbox = new OutboxStore(dbPath);
        outbox.Initialize();

        var settings = CreateSettings(placementMin: 1.0);
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, outbox, settings);
        var batchConfig = new BatchConfig("dev-1", "batch-1", "prod-1", 1.0);
        fsm.OnBatchStart(batchConfig, 0);

        var printer = new PrinterSimulator
        {
            Config = new PrinterSimConfig(ReceivedDelaySeconds: 0.05, CompletedDelaySeconds: 0.20)
        };

        double now = 0;
        fsm.PrintRequested += request => printer.Schedule(request, now);

        var sim = new WeightStreamSimulator(sampleRateHz: 10)
            .AddSegment(new WeightSegment(DurationSeconds: 1.0, BaseWeight: 0, NoiseSigma: 0.01))
            .AddSegment(new WeightSegment(DurationSeconds: 4.0, BaseWeight: 5.0, NoiseSigma: 0.01));

        foreach (var sample in sim.Run())
        {
            now = sample.TimeSeconds;
            fsm.OnWeightSample(sample);
            printer.Pump(now, fsm);
        }

        Assert.Equal(1, outbox.GetJobCount());
    }

    [Fact]
    public void ProductSwitchAppliesOnlyInWaitEmpty()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        var outbox = new OutboxStore(dbPath);
        outbox.Initialize();

        var settings = CreateSettings(placementMin: 1.0);
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, outbox, settings);
        fsm.OnBatchStart(new BatchConfig("dev-1", "batch-1", "prod-A", 1.0), 0);

        var now = 0.0;
        for (var i = 0; i < 8; i++)
        {
            now += 0.1;
            fsm.OnWeightSample(new WeightSample(2.0, "kg", now));
            if (fsm.State == FsmState.Loading)
            {
                fsm.OnProductSwitch("prod-B");
                break;
            }
        }

        Assert.Equal("prod-A", fsm.ActiveProductId);

        for (var i = 0; i < 12; i++)
        {
            now += 0.1;
            fsm.OnWeightSample(new WeightSample(0.0, "kg", now));
        }

        Assert.Equal(FsmState.WaitEmpty, fsm.State);
        Assert.Equal("prod-B", fsm.ActiveProductId);
    }

    [Fact]
    public void WeightChangeAfterPrintPausesReweighRequired()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        var outbox = new OutboxStore(dbPath);
        outbox.Initialize();

        var settings = CreateSettings(placementMin: 1.0);
        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, outbox, settings);
        fsm.OnBatchStart(new BatchConfig("dev-1", "batch-1", "prod-1", 1.0), 0);

        var printer = new PrinterSimulator
        {
            Config = new PrinterSimConfig(ReceivedDelaySeconds: 0.05, CompletedDelaySeconds: 10.0, DropCompleted: true)
        };

        double now = 0;
        fsm.PrintRequested += request => printer.Schedule(request, now);

        var sim = new WeightStreamSimulator(sampleRateHz: 10)
            .AddSegment(new WeightSegment(DurationSeconds: 1.0, BaseWeight: 0, NoiseSigma: 0.01))
            .AddSegment(new WeightSegment(DurationSeconds: 3.0, BaseWeight: 5.0, NoiseSigma: 0.01));

        foreach (var sample in sim.Run())
        {
            now = sample.TimeSeconds;
            fsm.OnWeightSample(sample);
            printer.Pump(now, fsm);
            if (fsm.State == FsmState.Printing)
            {
                break;
            }
        }

        now += 0.1;
        fsm.OnWeightSample(new WeightSample(5.5, "kg", now));

        Assert.Equal(FsmState.Paused, fsm.State);
        Assert.Equal(PauseReason.ReweighRequired, fsm.PauseReason);
    }

    private static StabilitySettings CreateSettings(double placementMin)
    {
        return new StabilitySettings(
            MedianDt: 0.1,
            Sigma: 0.01,
            Res: 0.01,
            Eps: 0.03,
            EpsAlign: 0.06,
            WindowSeconds: 1.0,
            EmptyThreshold: 0.05,
            PlacementMinWeight: placementMin,
            SlopeLimit: 0.02);
    }
}
