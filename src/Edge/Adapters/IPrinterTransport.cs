namespace ZebraBridge.Edge.Adapters;

public sealed record PrinterStatus(
    bool Ready,
    bool Busy,
    bool JobBufferEmpty,
    bool RfidOk,
    bool RfidUnknown,
    bool Paused,
    bool Error,
    bool Offline);

public interface IPrinterTransport
{
    bool SupportsStatusProbe { get; }
    Task SendAsync(string payload, CancellationToken cancellationToken);
    Task<PrinterStatus> ProbeStatusAsync(CancellationToken cancellationToken);
}
