namespace ZebraBridge.Core;

public interface IPrinterTransport
{
    Task SendAsync(byte[] data, CancellationToken cancellationToken = default);
}
