namespace ZebraBridge.Web;

public sealed class ScaleOptions
{
    public bool Enabled { get; set; } = true;
    public string? Port { get; set; }
    public int Baudrate { get; set; } = 9600;
    public int Bytesize { get; set; } = 8;
    public string Parity { get; set; } = "none";
    public double Stopbits { get; set; } = 1.0;
    public double TimeoutSec { get; set; } = 0.4;
    public string Unit { get; set; } = "kg";
    public double MinChange { get; set; } = 0.001;
    public double DetectTimeoutSec { get; set; } = 0.6;
    public double ReconnectIdleSec { get; set; } = 3.0;
    public bool PushEnabled { get; set; } = true;
    public int PushMinIntervalMs { get; set; } = 100;
    public double PushMinDelta { get; set; } = 0.001;
    public string PushEndpoint { get; set; } = "/api/method/rfidenter.rfidenter.api.ingest_scale_weight";
    public string? Device { get; set; }
    public bool AutoPrintEnabled { get; set; } = true;
    public int AutoPrintStableMs { get; set; } = 1000;
    public double AutoPrintPlacementMinWeight { get; set; } = 0.1;
    public double AutoPrintEmptyThreshold { get; set; } = 0.05;
    public double AutoPrintResetDelta { get; set; } = 0.1;
    public int AutoPrintPollMs { get; set; } = 200;

    public void ApplyEnvironment()
    {
        Enabled = OverrideBool(Enabled, "ZEBRA_SCALE_ENABLED");
        Port = Override(Port, "ZEBRA_SCALE_PORT");
        Baudrate = OverrideInt(Baudrate, "ZEBRA_SCALE_BAUDRATE");
        Bytesize = OverrideInt(Bytesize, "ZEBRA_SCALE_BYTESIZE");
        Parity = Override(Parity, "ZEBRA_SCALE_PARITY") ?? Parity;
        Stopbits = OverrideDouble(Stopbits, "ZEBRA_SCALE_STOPBITS");
        TimeoutSec = OverrideDouble(TimeoutSec, "ZEBRA_SCALE_TIMEOUT_SEC");
        Unit = Override(Unit, "ZEBRA_SCALE_UNIT") ?? Unit;
        MinChange = OverrideDouble(MinChange, "ZEBRA_SCALE_MIN_CHANGE");
        DetectTimeoutSec = OverrideDouble(DetectTimeoutSec, "ZEBRA_SCALE_DETECT_TIMEOUT_SEC");
        ReconnectIdleSec = OverrideDouble(ReconnectIdleSec, "ZEBRA_SCALE_RECONNECT_IDLE_SEC");
        PushEnabled = OverrideBool(PushEnabled, "ZEBRA_SCALE_PUSH_ENABLED");
        PushMinIntervalMs = OverrideInt(PushMinIntervalMs, "ZEBRA_SCALE_PUSH_MIN_INTERVAL_MS");
        PushMinDelta = OverrideDouble(PushMinDelta, "ZEBRA_SCALE_PUSH_MIN_DELTA");
        PushEndpoint = Override(PushEndpoint, "ZEBRA_SCALE_ERP_ENDPOINT") ?? PushEndpoint;
        Device = Override(Device, "ZEBRA_SCALE_DEVICE");
        AutoPrintEnabled = OverrideBool(AutoPrintEnabled, "ZEBRA_AUTOPRINT_ENABLED");
        AutoPrintStableMs = OverrideInt(AutoPrintStableMs, "ZEBRA_AUTOPRINT_STABLE_MS");
        AutoPrintPlacementMinWeight = OverrideDouble(AutoPrintPlacementMinWeight, "ZEBRA_AUTOPRINT_MIN_WEIGHT");
        AutoPrintEmptyThreshold = OverrideDouble(AutoPrintEmptyThreshold, "ZEBRA_AUTOPRINT_EMPTY_THRESHOLD");
        AutoPrintResetDelta = OverrideDouble(AutoPrintResetDelta, "ZEBRA_AUTOPRINT_RESET_DELTA");
        AutoPrintPollMs = OverrideInt(AutoPrintPollMs, "ZEBRA_AUTOPRINT_POLL_MS");
    }

    private static string? Override(string? current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        return string.IsNullOrWhiteSpace(raw) ? current : raw.Trim();
    }

    private static int OverrideInt(int current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        return string.IsNullOrWhiteSpace(raw) ? current : int.TryParse(raw.Trim(), out var parsed) ? parsed : current;
    }

    private static double OverrideDouble(double current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        return string.IsNullOrWhiteSpace(raw) ? current : double.TryParse(raw.Trim(), out var parsed) ? parsed : current;
    }

    private static bool OverrideBool(bool current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return current;
        }
        var s = raw.Trim().ToLowerInvariant();
        if (s is "1" or "true" or "yes" or "y" or "on")
        {
            return true;
        }
        if (s is "0" or "false" or "no" or "n" or "off")
        {
            return false;
        }
        return current;
    }
}
