namespace ZebraBridge.Edge;

public sealed record PrinterCapabilities(bool SupportsStatusQuery);

public interface IPrinterTransport
{
    PrinterCapabilities Capabilities { get; }
    Task SendAsync(PrintRequest request, CancellationToken cancellationToken);
}
