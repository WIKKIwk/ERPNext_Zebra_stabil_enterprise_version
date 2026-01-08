using LibUsbDotNet;
using LibUsbDotNet.Main;
using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed class LibUsbPrinterTransport : IPrinterTransceiver
{
    private readonly int _vendorId;
    private readonly int _productId;
    private readonly int _timeoutMs;
    private readonly object _sync = new();

    public LibUsbPrinterTransport(int vendorId, int productId, int timeoutMs)
    {
        _vendorId = vendorId;
        _productId = productId;
        _timeoutMs = Math.Clamp(timeoutMs, 500, 20000);
    }

    public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data is null || data.Length == 0)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            UseDevice((writer, reader) =>
            {
                var error = writer.Write(data, _timeoutMs, out var _);
                EnsureUsbOk(error, "USB write failed.");
            }, requireReader: false);
        }

        return Task.CompletedTask;
    }

    public Task<byte[]> TransceiveAsync(
        byte[] data,
        int readTimeoutMs,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (data is null || data.Length == 0)
        {
            throw new ZebraBridgeException("ZPL payload is required for transceive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var timeout = Math.Clamp(readTimeoutMs, 50, 20000);
        var bufferSize = Math.Clamp(maxBytes, 1, 262144);

        byte[] result;
        lock (_sync)
        {
            result = UseDevice((writer, reader) =>
            {
                if (reader is null)
                {
                    throw new PrinterUnsupportedOperationException("No USB bulk IN endpoint available.");
                }

                var writeError = writer.Write(data, _timeoutMs, out var _);
                EnsureUsbOk(writeError, "USB write failed.");

                var buffer = new byte[bufferSize];
                var readError = reader.Read(buffer, timeout, out var bytesRead);

                if (readError == ErrorCode.IoTimedOut)
                {
                    return Array.Empty<byte>();
                }

                EnsureUsbOk(readError, "USB read failed.");
                return bytesRead <= 0 ? Array.Empty<byte>() : buffer.Take(bytesRead).ToArray();
            }, requireReader: true);
        }

        return Task.FromResult(result);
    }

    private T UseDevice<T>(Func<UsbEndpointWriter, UsbEndpointReader?, T> action, bool requireReader)
    {
        UsbDevice? device = null;
        IUsbDevice? wholeDevice = null;

        try
        {
            device = UsbDevice.OpenUsbDevice(new UsbDeviceFinder(_vendorId, _productId));
            if (device is null)
            {
                throw new PrinterNotFoundException("USB printer not found.");
            }

            wholeDevice = device as IUsbDevice;
            if (wholeDevice is not null)
            {
                try
                {
                    wholeDevice.SetConfiguration(1);
                }
                catch
                {
                }
                try
                {
                    wholeDevice.ClaimInterface(0);
                }
                catch
                {
                }
            }

            using var writer = OpenWriter(device);
            using var reader = requireReader ? OpenReader(device) : null;
            return action(writer, reader);
        }
        finally
        {
            if (wholeDevice is not null)
            {
                try
                {
                    wholeDevice.ReleaseInterface(0);
                }
                catch
                {
                }
            }

            try
            {
                device?.Close();
            }
            catch
            {
            }

            try
            {
                UsbDevice.Exit();
            }
            catch
            {
            }
        }
    }

    private static UsbEndpointWriter OpenWriter(UsbDevice device)
    {
        var candidates = new[]
        {
            WriteEndpointID.Ep01,
            WriteEndpointID.Ep02,
            WriteEndpointID.Ep03,
            WriteEndpointID.Ep04
        };

        foreach (var endpoint in candidates)
        {
            try
            {
                return device.OpenEndpointWriter(endpoint);
            }
            catch
            {
            }
        }

        throw new PrinterCommunicationException("No USB bulk OUT endpoint found.");
    }

    private static UsbEndpointReader? OpenReader(UsbDevice device)
    {
        var candidates = new[]
        {
            ReadEndpointID.Ep01,
            ReadEndpointID.Ep02,
            ReadEndpointID.Ep03,
            ReadEndpointID.Ep04
        };

        foreach (var endpoint in candidates)
        {
            try
            {
                return device.OpenEndpointReader(endpoint);
            }
            catch
            {
            }
        }

        return null;
    }

    private static void EnsureUsbOk(ErrorCode error, string message)
    {
        if (error != ErrorCode.None)
        {
            throw new PrinterCommunicationException($"{message} ({error}).");
        }
    }
}
