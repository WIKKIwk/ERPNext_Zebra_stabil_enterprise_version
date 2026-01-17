using ZebraBridge.Edge;

namespace ZebraBridge.Edge.Stability;

public sealed class StabilityDetector
{
    private readonly StabilitySettings _settings;
    private readonly Queue<(double Time, double Value)> _window = new();
    private readonly Queue<(double Time, double Value)> _slowHistory = new();
    private readonly Queue<double> _medianBuffer = new();
    private readonly Queue<double> _dtWindow = new();
    private readonly List<double> _relearnBuffer = new();
    private double? _lastMono;
    private int _spikeCount;
    private bool _relearnMode;
    private int _totalSamples;

    public StabilityDetector(StabilitySettings settings)
    {
        _settings = settings;
        MedianDt = 0.10;
    }

    public double Fast { get; private set; }
    public double Slow { get; private set; }
    public bool IsStable { get; private set; }
    public double Mean { get; private set; }
    public double Range { get; private set; }
    public int SampleCount => _window.Count;
    public int TotalSamples => _totalSamples;
    public double WindowSpanSeconds { get; private set; }
    public double MedianDt { get; private set; }

    public void Reset()
    {
        _window.Clear();
        _slowHistory.Clear();
        _medianBuffer.Clear();
        _dtWindow.Clear();
        _relearnBuffer.Clear();
        _lastMono = null;
        _spikeCount = 0;
        _relearnMode = false;
        _totalSamples = 0;
        Fast = 0;
        Slow = 0;
        IsStable = false;
        Mean = 0;
        Range = 0;
        WindowSpanSeconds = 0;
    }

    public void AddSample(WeightSample sample)
    {
        if (!sample.Valid)
        {
            return;
        }

        if (_lastMono.HasValue)
        {
            var dt = sample.MonoTimeSeconds - _lastMono.Value;
            _lastMono = sample.MonoTimeSeconds;

            if (dt <= 0)
            {
                return;
            }

            if (_relearnMode)
            {
                if (dt > 3 * MedianDt)
                {
                    return;
                }

                _relearnBuffer.Add(dt);
                if (_relearnBuffer.Count == 5)
                {
                    _dtWindow.Clear();
                    foreach (var value in _relearnBuffer)
                    {
                        _dtWindow.Enqueue(value);
                    }
                    MedianDt = Median(_dtWindow);
                    _relearnBuffer.Clear();
                    _relearnMode = false;
                }
            }
            else if (dt > 3 * MedianDt)
            {
                _spikeCount++;
                if (_spikeCount >= 5)
                {
                    _relearnMode = true;
                    _relearnBuffer.Clear();
                    _spikeCount = 0;
                }
                return;
            }
            else
            {
                _spikeCount = 0;
                _dtWindow.Enqueue(dt);
                while (_dtWindow.Count > 21)
                {
                    _dtWindow.Dequeue();
                }
                MedianDt = Median(_dtWindow);
            }
        }
        else
        {
            _lastMono = sample.MonoTimeSeconds;
            return;
        }

        var m = ApplyMedian(sample.Value);
        var dtForEma = MedianDt;
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

        _totalSamples++;
        _window.Enqueue((sample.MonoTimeSeconds, m));
        _slowHistory.Enqueue((sample.MonoTimeSeconds, Slow));
        TrimWindow(sample.MonoTimeSeconds);
        TrimSlowHistory(sample.MonoTimeSeconds);
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

        return Median(_medianBuffer);
    }

    private void TrimWindow(double now)
    {
        const double epsilon = 1e-9;
        while (_window.Count > 0 && now - _window.Peek().Time > _settings.WindowSeconds + epsilon)
        {
            _window.Dequeue();
        }

        if (_window.Count > 0)
        {
            WindowSpanSeconds = now - _window.Peek().Time;
        }
        else
        {
            WindowSpanSeconds = 0;
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

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var mid = sorted.Length / 2;
        if (sorted.Length % 2 == 0)
        {
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        return sorted[mid];
    }
}
