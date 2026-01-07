using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public static class StatePaths
{
    public static string GetStateDir()
    {
        var explicitDir = Environment.GetEnvironmentVariable("ZEBRA_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return ExpandHome(explicitDir);
        }

        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgState))
        {
            return Path.Combine(ExpandHome(xdgState), "zebra-bridge");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "zebra-bridge");
    }

    public static string GetStateDir(EpcGeneratorOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.StateDir))
        {
            return ExpandHome(options.StateDir);
        }

        return GetStateDir();
    }

    public static string GetEpcGeneratorStatePath(EpcGeneratorOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.StatePath))
        {
            return ExpandHome(options.StatePath);
        }

        return Path.Combine(GetStateDir(options), "epc-generator.json");
    }

    public static string GetErpConfigPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ZEBRA_ERP_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return ExpandHome(explicitPath);
        }

        return Path.Combine(GetStateDir(), "erp-config.json");
    }

    private static string ExpandHome(string path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.StartsWith("~", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var suffix = trimmed[1..];
        if (suffix.StartsWith(Path.DirectorySeparatorChar) || suffix.StartsWith(Path.AltDirectorySeparatorChar))
        {
            suffix = suffix[1..];
        }

        return string.IsNullOrWhiteSpace(suffix)
            ? home
            : Path.Combine(home, suffix);
    }
}
