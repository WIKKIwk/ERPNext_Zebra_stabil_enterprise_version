namespace ZebraBridge.Web;

public sealed class ErpAgentOptions
{
    public bool Enabled { get; set; } = true;
    public string? BaseUrl { get; set; }
    public string? Auth { get; set; }
    public string? Secret { get; set; }
    public string? AgentId { get; set; }
    public string? Device { get; set; }
    public string RegisterEndpoint { get; set; } = "/api/method/rfidenter.rfidenter.api.register_agent";
    public string PollEndpoint { get; set; } = "/api/method/rfidenter.rfidenter.api.agent_poll";
    public string ReplyEndpoint { get; set; } = "/api/method/rfidenter.rfidenter.api.agent_reply";

    public void ApplyEnvironment()
    {
        Enabled = OverrideBool(Enabled, "ZEBRA_ERP_AGENT_ENABLED");
        BaseUrl = Override(BaseUrl, "ZEBRA_ERP_URL");
        Auth = Override(Auth, "ZEBRA_ERP_AUTH");
        Secret = Override(Secret, "ZEBRA_ERP_SECRET");
        AgentId = Override(AgentId, "ZEBRA_ERP_AGENT_ID");
        Device = Override(Device, "ZEBRA_ERP_DEVICE");
        RegisterEndpoint = Override(RegisterEndpoint, "ZEBRA_ERP_REGISTER_ENDPOINT") ?? RegisterEndpoint;
        PollEndpoint = Override(PollEndpoint, "ZEBRA_ERP_POLL_ENDPOINT") ?? PollEndpoint;
        ReplyEndpoint = Override(ReplyEndpoint, "ZEBRA_ERP_REPLY_ENDPOINT") ?? ReplyEndpoint;
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
}
