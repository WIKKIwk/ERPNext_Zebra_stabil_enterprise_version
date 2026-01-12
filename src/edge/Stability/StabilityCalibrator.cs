namespace ZebraBridge.Edge;

public static class StabilityCalibrator
{
    public static StabilitySettings FromEmptyLog(
        IReadOnlyList<WeightSample> emptyLog,
        double placementMinConfig)
    {
        if (emptyLog.Count < 3)
        {
            return new StabilitySettings(
                MedianDt: 0.1,
                Sigma: 0.01,
                Res: 0.01,
                Eps: 0.03,
                EpsAlign: 0.06,
                WindowSeconds: 0.8,
                EmptyThreshold: 0.03,
                PlacementMinWeight: Math.Max(placementMinConfig, 0.05),
                SlopeLimit: 0.025);
        }

        var values = emptyLog.Where(s => s.Valid && s.Connected)
            .Select(s => s.Value).ToArray();
        if (values.Length < 3)
        {
            return new StabilitySettings(
                MedianDt: 0.1,
                Sigma: 0.01,
                Res: 0.01,
                Eps: 0.03,
                EpsAlign: 0.06,
                WindowSeconds: 0.8,
                EmptyThreshold: 0.03,
                PlacementMinWeight: Math.Max(placementMinConfig, 0.05),
                SlopeLimit: 0.025);
        }

        var median = Median(values);
        var absDev = values.Select(v => Math.Abs(v - median)).ToArray();
        var sigma = 1.4826 * Median(absDev);

        var diffs = new List<double>();
        for (var i = 1; i < values.Length; i++)
        {
            var diff = Math.Abs(values[i] - values[i - 1]);
            if (diff > 0)
            {
                diffs.Add(diff);
            }
        }

        var res = diffs.Count > 0 ? diffs.Min() : Math.Max(0.001, sigma / 10);

        var dtValues = new List<double>();
        for (var i = 1; i < emptyLog.Count; i++)
        {
            var dt = emptyLog[i].TimeSeconds - emptyLog[i - 1].TimeSeconds;
            if (dt > 0)
            {
                dtValues.Add(dt);
            }
        }

        var medianDt = dtValues.Count > 0 ? Median(dtValues) : 0.1;

        var eps = Math.Max(3 * sigma, 2 * res);
        var epsAlign = Math.Max(2 * eps, Math.Max(2 * sigma, 3 * res));
        var window = Math.Max(0.80, 30 * medianDt);
        var emptyThresh = Math.Max(3 * sigma, 2 * res);
        var placementMin = Math.Max(placementMinConfig, Math.Max(5 * sigma, 2 * res));
        var slopeLimit = 2 * sigma / window;

        return new StabilitySettings(
            MedianDt: medianDt,
            Sigma: sigma,
            Res: res,
            Eps: eps,
            EpsAlign: epsAlign,
            WindowSeconds: window,
            EmptyThreshold: emptyThresh,
            PlacementMinWeight: placementMin,
            SlopeLimit: slopeLimit);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        if (sorted.Length % 2 == 0)
        {
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        return sorted[mid];
    }
}
