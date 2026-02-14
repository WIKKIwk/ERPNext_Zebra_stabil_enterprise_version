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
            ? null
            : options.DevicePath.Trim();

        if (!string.IsNullOrWhiteSpace(devicePath) && !File.Exists(devicePath))
        {
            devicePath = null;
        }

        devicePath ??= DetectLinuxDevicePath();

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

        var connected = IsDeviceReady(devicePath);
        var detail = connected ? "device present" : $"device not ready: {devicePath}";
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
            if (IsDeviceReady(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// File.Exists faqat fayl borligini tekshiradi — Docker /dev mount qilganda
    /// device fayl har doim mavjud, printer ulanmagan bo'lsa ham.
    /// Bu metod device faylni ochishga urinib, real printer mavjudligini aniqlaydi.
    /// </summary>
    private static bool IsDeviceReady(string devicePath)
    {
        if (!File.Exists(devicePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                devicePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
