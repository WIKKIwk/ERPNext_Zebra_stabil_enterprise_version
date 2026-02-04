using System.Text.Json;
using ZebraBridge.Infrastructure;

namespace ZebraBridge.Web;

public sealed record ErpAgentRuntimeConfig(
    string Name,
    string BaseUrl,
    string AuthHeader,
    string Secret,
    string AgentId,
    string Device,
    string RegisterEndpoint,
    string PollEndpoint,
    string ReplyEndpoint,
    int HeartbeatIntervalMs,
    int PollIntervalMs,
    int PollWaitMs,
    int PollMax,
    string Version);

public static class ErpAgentConfigLoader
{
    public static IReadOnlyList<ErpAgentRuntimeConfig> Load(ErpAgentOptions options)
    {
        if (!options.Enabled)
        {
            return Array.Empty<ErpAgentRuntimeConfig>();
        }

        var fileConfigs = LoadFromConfigFile(options);
        if (fileConfigs.Count > 0)
        {
            return fileConfigs;
        }

        var jsonConfigs = LoadFromTargetsJson(options);
        if (jsonConfigs.Count > 0)
        {
            return jsonConfigs;
        }

        var suffixConfigs = LoadFromSuffixTargets(options);
        if (suffixConfigs.Count > 0)
        {
            return suffixConfigs;
        }

        return LoadFromSingleTarget(options);
    }

    private static List<ErpAgentRuntimeConfig> LoadFromConfigFile(ErpAgentOptions options)
    {
        var path = StatePaths.GetErpConfigPath();
        if (!File.Exists(path))
        {
            return new List<ErpAgentRuntimeConfig>();
        }

        try
        {
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<ErpAgentRuntimeConfig>();
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("erp", out var erpElement) && erpElement.ValueKind == JsonValueKind.Object)
            {
                var profiles = erpElement.TryGetProperty("profiles", out var profilesElement) &&
                               profilesElement.ValueKind == JsonValueKind.Object
                    ? profilesElement
                    : default;

                var activeProfile = GetString(erpElement, "activeProfile", GetString(erpElement, "active_profile", "local"));
                var profileElement = default(JsonElement);

                if (profiles.ValueKind == JsonValueKind.Object && profiles.TryGetProperty(activeProfile, out profileElement))
                {
                    var config = BuildFromProfile(options, activeProfile, erpElement, profileElement);
                    return config is not null ? new List<ErpAgentRuntimeConfig> { config } : new List<ErpAgentRuntimeConfig>();
                }

                if (profiles.ValueKind == JsonValueKind.Object)
                {
                    foreach (var profile in profiles.EnumerateObject())
                    {
                        var config = BuildFromProfile(options, profile.Name, erpElement, profile.Value);
                        return config is not null ? new List<ErpAgentRuntimeConfig> { config } : new List<ErpAgentRuntimeConfig>();
                    }
                }
            }

            if (root.TryGetProperty("targets", out var targetsElement) && targetsElement.ValueKind == JsonValueKind.Array)
            {
                return BuildFromTargets(options, targetsElement);
            }
        }
        catch
        {
            return new List<ErpAgentRuntimeConfig>();
        }

        return new List<ErpAgentRuntimeConfig>();
    }

    private static List<ErpAgentRuntimeConfig> LoadFromTargetsJson(ErpAgentOptions options)
    {
        var raw = Environment.GetEnvironmentVariable("ZEBRA_ERP_TARGETS_JSON");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<ErpAgentRuntimeConfig>();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<ErpAgentRuntimeConfig>();
            }

            return BuildFromTargets(options, doc.RootElement);
        }
        catch
        {
            return new List<ErpAgentRuntimeConfig>();
        }
    }

    private static List<ErpAgentRuntimeConfig> LoadFromSuffixTargets(ErpAgentOptions options)
    {
        var configs = new List<ErpAgentRuntimeConfig>();
        var local = BuildFromSuffix(options, "local", "LOCAL");
        if (local is not null)
        {
            configs.Add(local);
        }

        var server = BuildFromSuffix(options, "server", "SERVER");
        if (server is not null)
        {
            configs.Add(server);
        }

        return configs;
    }

    private static List<ErpAgentRuntimeConfig> LoadFromSingleTarget(ErpAgentOptions options)
    {
        var config = BuildConfig(
            options,
            name: "default",
            baseUrl: options.BaseUrl,
            auth: options.Auth,
            secret: options.Secret,
            agentId: options.AgentId,
            device: options.Device,
            enabled: true,
            heartbeatMs: options.HeartbeatIntervalMs,
            pollMs: options.PollIntervalMs,
            pollWaitMs: options.PollWaitMs,
            pollMax: options.PollMax,
            version: options.Version);

        return config is not null ? new List<ErpAgentRuntimeConfig> { config } : new List<ErpAgentRuntimeConfig>();
    }

    private static ErpAgentRuntimeConfig? BuildFromProfile(
        ErpAgentOptions options,
        string name,
        JsonElement root,
        JsonElement profile)
    {
        var rpcEnabled = GetBool(profile, "rpcEnabled", GetBool(profile, "rpc_enabled", true));
        if (!rpcEnabled)
        {
            return null;
        }

        var overrideEnv = GetBool(profile, "overrideEnv", GetBool(profile, "override_env", true));

        var baseUrl = GetString(profile, "baseUrl", GetString(profile, "base_url", GetString(root, "baseUrl", "")));
        var auth = GetString(profile, "auth", GetString(profile, "authorization", GetString(root, "auth", "")));
        var secret = GetString(profile, "secret", GetString(root, "secret", ""));
        var agentId = GetString(profile, "agentId", GetString(profile, "agent_id", ""));
        var device = GetString(profile, "device", GetString(root, "device", ""));
        var enabled = GetBool(profile, "enabled", true);

        if (!overrideEnv)
        {
            baseUrl = Environment.GetEnvironmentVariable("ZEBRA_ERP_URL") ?? baseUrl;
            auth = Environment.GetEnvironmentVariable("ZEBRA_ERP_AUTH") ?? auth;
            secret = Environment.GetEnvironmentVariable("ZEBRA_ERP_SECRET") ?? secret;
            agentId = Environment.GetEnvironmentVariable("ZEBRA_ERP_AGENT_ID") ?? agentId;
            device = Environment.GetEnvironmentVariable("ZEBRA_ERP_DEVICE") ?? device;
        }

        var heartbeatMs = GetInt(profile, "heartbeatMs", options.HeartbeatIntervalMs);
        var pollMs = GetInt(profile, "pollMs", options.PollIntervalMs);
        var pollWaitMs = GetInt(profile, "pollWaitMs", GetInt(profile, "poll_wait_ms", options.PollWaitMs));
        var pollMax = GetInt(profile, "pollMax", options.PollMax);
        var version = GetString(profile, "version", options.Version);

        return BuildConfig(options, name, baseUrl, auth, secret, agentId, device, enabled, heartbeatMs, pollMs, pollWaitMs, pollMax, version);
    }

    private static List<ErpAgentRuntimeConfig> BuildFromTargets(ErpAgentOptions options, JsonElement targets)
    {
        var configs = new List<ErpAgentRuntimeConfig>();
        var index = 0;
        foreach (var target in targets.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            index += 1;
            var name = GetString(target, "name", $"target{index}");
            var baseUrl = GetString(target, "url", GetString(target, "baseUrl", GetString(target, "base_url", "")));
            var auth = GetString(target, "auth", GetString(target, "authorization", ""));
            var secret = GetString(target, "secret", GetString(target, "rfidenter_token", ""));
            var agentId = GetString(target, "agentId", GetString(target, "agent_id", ""));
            var device = GetString(target, "device", "");
            var enabled = GetBool(target, "enabled", true);
            var heartbeatMs = GetInt(target, "heartbeatMs", options.HeartbeatIntervalMs);
            var pollMs = GetInt(target, "pollMs", options.PollIntervalMs);
            var pollWaitMs = GetInt(target, "pollWaitMs", GetInt(target, "poll_wait_ms", options.PollWaitMs));
            var pollMax = GetInt(target, "pollMax", options.PollMax);
            var version = GetString(target, "version", options.Version);

            var config = BuildConfig(options, name, baseUrl, auth, secret, agentId, device, enabled, heartbeatMs, pollMs, pollWaitMs, pollMax, version);
            if (config is not null)
            {
                configs.Add(config);
            }
        }

        return configs;
    }

    private static ErpAgentRuntimeConfig? BuildFromSuffix(ErpAgentOptions options, string name, string suffix)
    {
        var baseUrl = Environment.GetEnvironmentVariable($"ZEBRA_ERP_URL_{suffix}")
                      ?? Environment.GetEnvironmentVariable($"ZEBRA_ERP_{suffix}_URL");
        var auth = Environment.GetEnvironmentVariable($"ZEBRA_ERP_AUTH_{suffix}")
                   ?? Environment.GetEnvironmentVariable($"ZEBRA_ERP_{suffix}_AUTH");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(auth))
        {
            return null;
        }

        var secret = Environment.GetEnvironmentVariable($"ZEBRA_ERP_SECRET_{suffix}")
                     ?? Environment.GetEnvironmentVariable($"ZEBRA_ERP_{suffix}_SECRET")
                     ?? options.Secret;

        var device = Environment.GetEnvironmentVariable($"ZEBRA_ERP_DEVICE_{suffix}")
                     ?? Environment.GetEnvironmentVariable($"ZEBRA_ERP_{suffix}_DEVICE")
                     ?? options.Device;

        var agentId = Environment.GetEnvironmentVariable($"ZEBRA_ERP_AGENT_ID_{suffix}")
                      ?? Environment.GetEnvironmentVariable($"ZEBRA_ERP_{suffix}_AGENT_ID")
                      ?? options.AgentId;

        var enabledRaw = Environment.GetEnvironmentVariable($"ZEBRA_ERP_AGENT_ENABLED_{suffix}");
        var enabled = ParseBool(enabledRaw, true);

        return BuildConfig(options, name, baseUrl, auth, secret, agentId, device, enabled,
            options.HeartbeatIntervalMs, options.PollIntervalMs, options.PollWaitMs, options.PollMax, options.Version);
    }

    private static ErpAgentRuntimeConfig? BuildConfig(
        ErpAgentOptions options,
        string name,
        string? baseUrl,
        string? auth,
        string? secret,
        string? agentId,
        string? device,
        bool enabled,
        int heartbeatMs,
        int pollMs,
        int pollWaitMs,
        int pollMax,
        string? version)
    {
        if (!enabled)
        {
            return null;
        }

        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedAuth = NormalizeAuth(auth);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl) || string.IsNullOrWhiteSpace(normalizedAuth))
        {
            return null;
        }

        var machine = Environment.MachineName;
        var finalAgentId = string.IsNullOrWhiteSpace(agentId) ? $"zebra-{machine}-{name}" : agentId.Trim();
        var finalDevice = string.IsNullOrWhiteSpace(device) ? machine : device.Trim();

        return new ErpAgentRuntimeConfig(
            name,
            normalizedBaseUrl,
            normalizedAuth,
            secret ?? string.Empty,
            finalAgentId,
            finalDevice,
            options.RegisterEndpoint,
            options.PollEndpoint,
            options.ReplyEndpoint,
            Clamp(heartbeatMs, 2000, 60000),
            Clamp(pollMs, 150, 5000),
            Clamp(pollWaitMs, 0, 15000),
            Clamp(pollMax, 1, 25),
            string.IsNullOrWhiteSpace(version) ? options.Version : version.Trim()
        );
    }

    private static string NormalizeBaseUrl(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }
        return value.EndsWith("/", StringComparison.Ordinal) ? value[..^1] : value;
    }

    private static string NormalizeAuth(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return value.StartsWith("token ", StringComparison.OrdinalIgnoreCase) ? value : $"token {value}";
    }

    private static string GetString(JsonElement element, string name, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    private static bool GetBool(JsonElement element, string name, bool fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed != 0,
            _ => fallback
        };
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static bool ParseBool(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
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
        return fallback;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }
        return value > max ? max : value;
    }
}
