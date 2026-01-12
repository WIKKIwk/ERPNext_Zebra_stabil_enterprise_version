namespace ZebraBridge.Edge;

public sealed class StabilityDetector
{
    private readonly StabilitySettings _settings;
    private readonly Queue<(double Time, double Value)> _window = new();
    private readonly Queue<(double Time, double Value)> _slowHistory = new();
    private readonly Queue<double> _medianBuffer = new();
    private double? _lastTime;

    public StabilityDetector(StabilitySettings settings)
    {
        _settings = settings;
    }

    public double Fast { get; private set; }
    public double Slow { get; private set; }
    public bool IsStable { get; private set; }
    public double Mean { get; private set; }
    public double Range { get; private set; }
    public int SampleCount => _window.Count;
    public double WindowSpanSeconds { get; private set; }

    public void Reset()
    {
        _window.Clear();
        _slowHistory.Clear();
        _medianBuffer.Clear();
        _lastTime = null;
        Fast = 0;
        Slow = 0;
        IsStable = false;
        Mean = 0;
        Range = 0;
        WindowSpanSeconds = 0;
    }

    public void AddSample(WeightSample sample)
    {
        if (!sample.Valid || !sample.Connected)
        {
            return;
        }

        if (_lastTime.HasValue)
        {
            var dt = sample.TimeSeconds - _lastTime.Value;
            if (dt <= 0)
            {
                _lastTime = sample.TimeSeconds;
                return;
            }

            if (dt > 3 * _settings.MedianDt)
            {
                _lastTime = sample.TimeSeconds;
                return;
            }
        }

        _lastTime = sample.TimeSeconds;

        var m = ApplyMedian(sample.Value);
        var dtForEma = _settings.MedianDt;
        var alphaFast = 1 - Math.Exp(-dtForEma / 0.20);
        var alphaSlow = 1 - Math.Exp(-dtForEma / 1.00);

        if (_slowHistory.Count == 0)
        {
            Fast = m;
            Slow = m;
        }
        else
        {
            Fast = alphaFast * m + (1 - alphaFast) * Fast;
            Slow = alphaSlow * m + (1 - alphaSlow) * Slow;
        }

        _window.Enqueue((sample.TimeSeconds, m));
        _slowHistory.Enqueue((sample.TimeSeconds, Slow));
        TrimWindow(sample.TimeSeconds);
        TrimSlowHistory(sample.TimeSeconds);
        RecomputeWindowStats();
        EvaluateStability();
    }

    private double ApplyMedian(double value)
    {
        _medianBuffer.Enqueue(value);
        while (_medianBuffer.Count > 5)
        {
            _medianBuffer.Dequeue();
        }

        var sorted = _medianBuffer.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private void TrimWindow(double now)
    {
        while (_window.Count > 0 && now - _window.Peek().Time > _settings.WindowSeconds)
        {
            _window.Dequeue();
        }

        if (_window.Count > 0)
        {
            WindowSpanSeconds = now - _window.Peek().Time;
        }
    }

    private void TrimSlowHistory(double now)
    {
        while (_slowHistory.Count > 0 && now - _slowHistory.Peek().Time > _settings.WindowSeconds)
        {
            _slowHistory.Dequeue();
        }
    }

    private void RecomputeWindowStats()
    {
        if (_window.Count == 0)
        {
            Mean = 0;
            Range = 0;
            return;
        }

        var values = _window.Select(s => s.Value).ToArray();
        Mean = values.Average();
        Range = values.Max() - values.Min();
    }

    private void EvaluateStability()
    {
        if (_window.Count == 0 || WindowSpanSeconds < _settings.WindowSeconds)
        {
            IsStable = false;
            return;
        }

        var slope = ComputeSlope();
        IsStable = Mean >= _settings.PlacementMinWeight
            && Range <= _settings.Eps
            && Math.Abs(Fast - Slow) <= _settings.EpsAlign
            && Math.Abs(slope) <= _settings.SlopeLimit;
    }

    private double ComputeSlope()
    {
        if (_slowHistory.Count < 2)
        {
            return 0;
        }

        var oldest = _slowHistory.Peek();
        var newest = _slowHistory.Last();
        var dt = newest.Time - oldest.Time;
        if (dt <= 0)
        {
            return 0;
        }

        return (newest.Value - oldest.Value) / dt;
    }
}
