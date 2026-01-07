namespace ZebraBridge.Core;

public interface IPrinterTransceiver : IPrinterTransport
{
    Task<byte[]> TransceiveAsync(
        byte[] data,
        int readTimeoutMs,
        int maxBytes,
        CancellationToken cancellationToken = default);
}
