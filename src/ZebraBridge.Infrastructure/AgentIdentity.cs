using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace ZebraBridge.Infrastructure;

public static class AgentIdentity
{
    public static string GetStableAgentUid()
    {
        try
        {
            var macs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Select(nic => nic.GetPhysicalAddress()?.ToString())
                .Where(mac => !string.IsNullOrWhiteSpace(mac) && mac != "000000000000")
                .Select(mac => mac!.ToLowerInvariant())
                .Distinct()
                .OrderBy(mac => mac)
                .ToList();

            var seed = macs.Count > 0 ? string.Join(",", macs) : Environment.MachineName;
            return Sha1Hex(seed)[..12];
        }
        catch
        {
            return Sha1Hex(Environment.MachineName)[..12];
        }
    }

    private static string Sha1Hex(string value)
    {
        using var sha1 = SHA1.Create();
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = sha1.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
