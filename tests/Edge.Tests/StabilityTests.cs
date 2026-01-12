using ZebraBridge.Edge;
using ZebraBridge.Edge.Stability;
using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class StabilityTests
{
    [Fact]
    public void StableWithNoise()
    {
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var now = 0.0;

        for (var i = 0; i < 30; i++)
        {
            now += 0.1;
            detector.AddSample(new WeightSample(5.0 + (i % 2 == 0 ? 0.01 : -0.01), "kg", now));
        }

        Assert.True(detector.IsStable);
    }

    [Fact]
    public void SpikeSamplesAreDropped()
    {
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var now = 0.0;

        for (var i = 0; i < 10; i++)
        {
            now += 0.1;
            detector.AddSample(new WeightSample(1.0, "kg", now));
        }

        var countBefore = detector.SampleCount;
        now += 1.0; // spike: dt > 3*median_dt
        detector.AddSample(new WeightSample(1.0, "kg", now));

        Assert.Equal(countBefore, detector.SampleCount);
    }

    [Fact]
    public void RelearnAfterFiveSpikes()
    {
        var settings = CreateSettings();
        var detector = new StabilityDetector(settings);
        var now = 0.0;

        for (var i = 0; i < 10; i++)
        {
            now += 0.1;
            detector.AddSample(new WeightSample(1.0, "kg", now));
        }

        for (var i = 0; i < 5; i++)
        {
            now += 1.0;
            detector.AddSample(new WeightSample(1.0, "kg", now));
        }

        for (var i = 0; i < 5; i++)
        {
            now += 0.2;
            detector.AddSample(new WeightSample(1.0, "kg", now));
        }

        Assert.InRange(detector.MedianDt, 0.19, 0.21);
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
