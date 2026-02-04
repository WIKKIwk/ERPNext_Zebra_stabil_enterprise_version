namespace ZebraBridge.Web;

public sealed class ErpAgentOptions
{
    public bool Enabled { get; set; } = true;
    public string? BaseUrl { get; set; }
    public string? Auth { get; set; }
    public string? Secret { get; set; }
    public string? AgentId { get; set; }
    public string? AgentUid { get; set; }
    public string? Device { get; set; }
    public string RegisterEndpoint { get; set; } = "/api/method/rfidenter.rfidenter.api.register_agent";
    public string PollEndpoint { get; set; } = "/api/method/rfidenter.rfidenter.api.agent_poll";
    public string ReplyEndpoint { get; set; } = "/api/method/rfidenter.rfidenter.api.agent_reply";
    public int HeartbeatIntervalMs { get; set; } = 10_000;
    public int PollIntervalMs { get; set; } = 800;
    public int PollWaitMs { get; set; } = 1500;
    public int PollMax { get; set; } = 5;
    public string Version { get; set; } = "zebra-bridge-v1";

    public void ApplyEnvironment()
    {
        Enabled = OverrideBool(Enabled, "ZEBRA_ERP_AGENT_ENABLED");
        BaseUrl = Override(BaseUrl, "ZEBRA_ERP_URL");
        Auth = Override(Auth, "ZEBRA_ERP_AUTH");
        Secret = Override(Secret, "ZEBRA_ERP_SECRET");
        AgentId = Override(AgentId, "ZEBRA_ERP_AGENT_ID");
        AgentUid = Override(AgentUid, "ZEBRA_ERP_AGENT_UID");
        Device = Override(Device, "ZEBRA_ERP_DEVICE");
        RegisterEndpoint = Override(RegisterEndpoint, "ZEBRA_ERP_REGISTER_ENDPOINT") ?? RegisterEndpoint;
        PollEndpoint = Override(PollEndpoint, "ZEBRA_ERP_POLL_ENDPOINT") ?? PollEndpoint;
        ReplyEndpoint = Override(ReplyEndpoint, "ZEBRA_ERP_REPLY_ENDPOINT") ?? ReplyEndpoint;
        HeartbeatIntervalMs = OverrideInt(HeartbeatIntervalMs, "ZEBRA_ERP_HEARTBEAT_MS", "ERP_AGENT_INTERVAL_MS");
        PollIntervalMs = OverrideInt(PollIntervalMs, "ZEBRA_ERP_POLL_MS", "ERP_RPC_POLL_MS");
        PollWaitMs = OverrideInt(PollWaitMs, "ZEBRA_ERP_POLL_WAIT_MS", "ERP_RPC_POLL_WAIT_MS");
        PollMax = OverrideInt(PollMax, "ZEBRA_ERP_POLL_MAX", "ERP_RPC_POLL_MAX");
        Version = Override(Version, "ZEBRA_ERP_AGENT_VERSION") ?? Version;
    }

    private static string? Override(string? current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        return string.IsNullOrWhiteSpace(raw) ? current : raw.Trim();
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

    private static int OverrideInt(int current, params string[] envKeys)
    {
        foreach (var envKey in envKeys)
        {
            var raw = Environment.GetEnvironmentVariable(envKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            if (int.TryParse(raw.Trim(), out var parsed))
            {
                return parsed;
            }
        }
        return current;
    }
}
