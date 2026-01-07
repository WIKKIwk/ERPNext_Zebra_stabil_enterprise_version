using System.IO.Ports;

namespace ZebraBridge.Web;

public sealed record ScalePortInfo(string Device, string Description);

public static class ScalePortEnumerator
{
    public static IReadOnlyList<ScalePortInfo> ListPorts()
    {
        try
        {
            var ports = SerialPort.GetPortNames();
            return ports
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .Select(port => new ScalePortInfo(port, string.Empty))
                .ToList();
        }
        catch
        {
            return Array.Empty<ScalePortInfo>();
        }
    }
}
