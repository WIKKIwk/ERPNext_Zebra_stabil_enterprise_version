using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed class PrinterTransportFactory : IPrinterTransportFactory
{
    private readonly PrinterOptions _options;

    public PrinterTransportFactory(PrinterOptions options)
    {
        _options = options;
    }

    public IPrinterTransport Create()
    {
        if (OperatingSystem.IsWindows())
        {
            var printerName = string.IsNullOrWhiteSpace(_options.PrinterName)
                ? _options.DevicePath
                : _options.PrinterName;

            if (string.IsNullOrWhiteSpace(printerName))
            {
                throw new PrinterNotFoundException("Printer name is required on Windows.");
            }

            return new WindowsRawPrinterTransport(printerName!);
        }

        var devicePath = string.IsNullOrWhiteSpace(_options.DevicePath)
            ? DetectLinuxDevicePath()
            : _options.DevicePath;

        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new PrinterNotFoundException("No Zebra device path found.");
        }

        return new DeviceFilePrinterTransport(devicePath!);
    }

    private static string? DetectLinuxDevicePath()
    {
        var candidates = new[]
        {
            "/dev/usb/lp0",
            "/dev/usb/lp1",
            "/dev/usb/lp2",
            "/dev/usb/lp3",
            "/dev/usb/lp4",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
