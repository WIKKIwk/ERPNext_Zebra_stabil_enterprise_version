namespace ZebraBridge.Edge;

public sealed record WeightSegment(
    double DurationSeconds,
    double BaseWeight,
    double NoiseSigma = 0,
    double SpikeProbability = 0,
    double SpikeMagnitude = 0,
    double JitterMs = 0,
    double DropProbability = 0,
    bool Disconnect = false);

public sealed class WeightStreamSimulator
{
    private readonly List<WeightSegment> _segments = new();
    private readonly Random _random;
    private readonly double _sampleRateHz;

    public WeightStreamSimulator(double sampleRateHz = 10, int seed = 17)
    {
        _sampleRateHz = sampleRateHz;
        _random = new Random(seed);
    }

    public WeightStreamSimulator AddSegment(WeightSegment segment)
    {
        _segments.Add(segment);
        return this;
    }

    public IEnumerable<WeightSample> Run(double startTimeSeconds = 0, string unit = "kg")
    {
        var now = startTimeSeconds;
        var baseDt = 1.0 / _sampleRateHz;

        foreach (var segment in _segments)
        {
            var end = now + segment.DurationSeconds;
            while (now < end)
            {
                var jitter = segment.JitterMs > 0 ? NextUniform(-segment.JitterMs, segment.JitterMs) / 1000.0 : 0;
                var dt = Math.Max(0.001, baseDt + jitter);
                now += dt;

                if (segment.Disconnect)
                {
                    yield return new WeightSample(0, unit, now, Valid: false, Connected: false);
                    continue;
                }

                if (segment.DropProbability > 0 && _random.NextDouble() < segment.DropProbability)
                {
                    continue;
                }

                var noise = segment.NoiseSigma > 0 ? NextGaussian(0, segment.NoiseSigma) : 0;
                var spike = 0.0;
                if (segment.SpikeProbability > 0 && _random.NextDouble() < segment.SpikeProbability)
                {
                    spike = segment.SpikeMagnitude;
                }

                var value = segment.BaseWeight + noise + spike;
                yield return new WeightSample(value, unit, now);
            }
        }
    }

    private double NextUniform(double min, double max)
    {
        return min + (max - min) * _random.NextDouble();
    }

    private double NextGaussian(double mean, double sigma)
    {
        var u1 = Math.Max(1e-9, _random.NextDouble());
        var u2 = _random.NextDouble();
        var z0 = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return mean + sigma * z0;
    }
}
