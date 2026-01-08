using LibUsbDotNet;
using LibUsbDotNet.Main;
using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed record PrinterPresenceStatus(
    bool Connected,
    string Transport,
    string DevicePath,
    int VendorId,
    int ProductId,
    string Message);

public static class PrinterPresenceProbe
{
    public static PrinterPresenceStatus GetStatus(PrinterOptions options)
    {
        var transport = (options.Transport ?? "device").Trim().ToLowerInvariant();
        if (transport == "usb")
        {
            return GetUsbStatus(options, transport);
        }

        if (OperatingSystem.IsWindows())
        {
            var printerName = string.IsNullOrWhiteSpace(options.PrinterName)
                ? options.DevicePath
                : options.PrinterName;

            var configured = !string.IsNullOrWhiteSpace(printerName);
            var message = configured ? "printer configured" : "printer name missing";
            return new PrinterPresenceStatus(
                configured,
                transport,
                printerName ?? string.Empty,
                options.VendorId,
                options.ProductId,
                message);
        }

        var devicePath = string.IsNullOrWhiteSpace(options.DevicePath)
            ? DetectLinuxDevicePath()
            : options.DevicePath;

        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return new PrinterPresenceStatus(
                false,
                transport,
                string.Empty,
                options.VendorId,
                options.ProductId,
                "device path not found");
        }

        var connected = File.Exists(devicePath);
        var detail = connected ? "device present" : $"device missing: {devicePath}";
        return new PrinterPresenceStatus(
            connected,
            transport,
            devicePath,
            options.VendorId,
            options.ProductId,
            detail);
    }

    private static PrinterPresenceStatus GetUsbStatus(PrinterOptions options, string transport)
    {
        try
        {
            var finder = new UsbDeviceFinder(options.VendorId, options.ProductId);
            var devices = UsbDevice.AllDevices.FindAll(finder);
            var connected = devices.Count > 0;
            var message = connected ? "usb device present" : "usb device not found";
            return new PrinterPresenceStatus(
                connected,
                transport,
                string.Empty,
                options.VendorId,
                options.ProductId,
                message);
        }
        catch (Exception ex)
        {
            return new PrinterPresenceStatus(
                false,
                transport,
                string.Empty,
                options.VendorId,
                options.ProductId,
                $"usb probe error: {ex.Message}");
        }
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
