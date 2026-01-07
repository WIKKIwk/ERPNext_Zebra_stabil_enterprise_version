namespace ZebraBridge.Core;

public sealed class EpcGeneratorOptions
{
    public string? StateDir { get; set; }
    public string? StatePath { get; set; }
    public string? PrefixHex { get; set; }

    public void ApplyEnvironment()
    {
        StateDir = Override(StateDir, "ZEBRA_STATE_DIR");
        StatePath = Override(StatePath, "ZEBRA_EPC_STATE_PATH");
        PrefixHex = Override(PrefixHex, "ZEBRA_EPC_PREFIX_HEX");
    }

    private static string? Override(string? current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        return string.IsNullOrWhiteSpace(raw) ? current : raw.Trim();
    }
}
