using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed class DeviceFilePrinterTransport : IPrinterTransport
{
    private readonly string _devicePath;

    public DeviceFilePrinterTransport(string devicePath)
    {
        _devicePath = devicePath ?? string.Empty;
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_devicePath) || !File.Exists(_devicePath))
        {
            throw new PrinterNotFoundException($"Device path not found: '{_devicePath}'.");
        }

        try
        {
            await using var stream = new FileStream(
                _devicePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true
            );
            await stream.WriteAsync(data, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new PrinterCommunicationException("Failed to write to device file.", ex);
        }
    }
}
