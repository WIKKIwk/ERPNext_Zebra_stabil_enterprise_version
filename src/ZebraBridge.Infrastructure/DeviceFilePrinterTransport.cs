using System.Runtime.InteropServices;
using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed class DeviceFilePrinterTransport : IPrinterTransport
{
    private readonly string _devicePath;
    private readonly int _timeoutMs;

    public DeviceFilePrinterTransport(string devicePath, int timeoutMs = 5000)
    {
        _devicePath = devicePath ?? string.Empty;
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 5000;
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_devicePath) || !File.Exists(_devicePath))
        {
            throw new PrinterNotFoundException($"Device path not found: '{_devicePath}'.");
        }

        try
        {
            await WriteWithTimeoutAsync(data, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Write timed out — USB endpoint stalled
            Console.WriteLine($"[transport] write timed out after {_timeoutMs}ms, attempting USB reset");
            if (TryResetUsbDevice())
            {
                Console.WriteLine("[transport] USB reset succeeded, retrying write");
                await Task.Delay(1000, cancellationToken);
                try
                {
                    await WriteWithTimeoutAsync(data, cancellationToken);
                    return;
                }
                catch (Exception retryEx)
                {
                    throw new PrinterCommunicationException(
                        "Failed to write to device file after USB reset.", retryEx);
                }
            }

            throw new PrinterCommunicationException(
                $"Write timed out after {_timeoutMs}ms. USB may be stalled. Try reconnecting the printer.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[transport] write failed: {ex.Message}, attempting USB reset");
            if (TryResetUsbDevice())
            {
                Console.WriteLine("[transport] USB reset succeeded, retrying write");
                await Task.Delay(1000, cancellationToken);
                try
                {
                    await WriteWithTimeoutAsync(data, cancellationToken);
                    return;
                }
                catch (Exception retryEx)
                {
                    throw new PrinterCommunicationException(
                        "Failed to write to device file after USB reset.", retryEx);
                }
            }

            throw new PrinterCommunicationException("Failed to write to device file.", ex);
        }
    }

    private async Task WriteWithTimeoutAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeoutMs);

        await using var stream = new FileStream(
            _devicePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true
        );
        await stream.WriteAsync(data, timeoutCts.Token);
        await stream.FlushAsync(timeoutCts.Token);
    }

    /// <summary>
    /// USB qurilmasini sysfs orqali reset qiladi.
    /// /dev/usb/lp0 → /sys/class/usbmisc/lp0 → parent USB device → authorized 0/1 toggle.
    /// Docker --privileged rejimida ishlaydi.
    /// </summary>
    private bool TryResetUsbDevice()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            // /dev/usb/lp0 → lp0
            var lpName = Path.GetFileName(_devicePath);
            var sysMiscPath = $"/sys/class/usbmisc/{lpName}";

            if (!Directory.Exists(sysMiscPath))
            {
                Console.WriteLine($"[usb-reset] sysfs path not found: {sysMiscPath}");
                return TryResetViaIoctl();
            }

            // /sys/class/usbmisc/lp0/device → real sysfs device path
            var deviceLink = Path.Combine(sysMiscPath, "device");
            if (!Directory.Exists(deviceLink))
            {
                Console.WriteLine("[usb-reset] device link not found");
                return TryResetViaIoctl();
            }

            // Navigate up to find USB device with "authorized" file
            var dir = new DirectoryInfo(deviceLink);
            // Go to parent (USB interface → USB device)
            var usbDevice = dir.Parent;

            if (usbDevice == null)
            {
                Console.WriteLine("[usb-reset] parent USB device not found");
                return TryResetViaIoctl();
            }

            var authorizedFile = Path.Combine(usbDevice.FullName, "authorized");
            if (!File.Exists(authorizedFile))
            {
                Console.WriteLine($"[usb-reset] authorized file not found at {usbDevice.FullName}");
                return TryResetViaIoctl();
            }

            Console.WriteLine($"[usb-reset] toggling authorized at {authorizedFile}");
            File.WriteAllText(authorizedFile, "0");
            Thread.Sleep(300);
            File.WriteAllText(authorizedFile, "1");
            Thread.Sleep(800);

            Console.WriteLine("[usb-reset] sysfs reset completed");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[usb-reset] sysfs reset failed: {ex.Message}");
            return TryResetViaIoctl();
        }
    }

    /// <summary>
    /// USBDEVFS_RESET ioctl orqali USB qurilmasini reset qiladi.
    /// Bu sysfs ishlamagan holatda fallback sifatida ishlatiladi.
    /// </summary>
    private bool TryResetViaIoctl()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            // /dev/usb/lp0 uchun bus device ni topish
            var lpName = Path.GetFileName(_devicePath);
            var ueventPath = $"/sys/class/usbmisc/{lpName}/device/uevent";

            if (!File.Exists(ueventPath))
            {
                Console.WriteLine("[usb-reset] uevent not found for ioctl reset");
                return false;
            }

            var uevent = File.ReadAllText(ueventPath);
            string? busnum = null, devnum = null;

            foreach (var line in uevent.Split('\n'))
            {
                if (line.StartsWith("BUSNUM=", StringComparison.Ordinal))
                    busnum = line[7..];
                else if (line.StartsWith("DEVNUM=", StringComparison.Ordinal))
                    devnum = line[7..];
            }

            if (busnum == null || devnum == null)
            {
                // Try parent device uevent
                var parentUeventPath = $"/sys/class/usbmisc/{lpName}/device/../uevent";
                if (File.Exists(parentUeventPath))
                {
                    uevent = File.ReadAllText(parentUeventPath);
                    foreach (var line in uevent.Split('\n'))
                    {
                        if (line.StartsWith("BUSNUM=", StringComparison.Ordinal))
                            busnum = line[7..];
                        else if (line.StartsWith("DEVNUM=", StringComparison.Ordinal))
                            devnum = line[7..];
                    }
                }
            }

            if (busnum == null || devnum == null)
            {
                Console.WriteLine("[usb-reset] BUSNUM/DEVNUM not found");
                return false;
            }

            var usbDevPath = $"/dev/bus/usb/{busnum.Trim().PadLeft(3, '0')}/{devnum.Trim().PadLeft(3, '0')}";
            if (!File.Exists(usbDevPath))
            {
                Console.WriteLine($"[usb-reset] USB device not found: {usbDevPath}");
                return false;
            }

            Console.WriteLine($"[usb-reset] USBDEVFS_RESET on {usbDevPath}");

            const uint USBDEVFS_RESET = 21780; // ('U' << 8) | 20
            const int O_WRONLY = 1;

            var fd = open(usbDevPath, O_WRONLY);
            if (fd < 0)
            {
                Console.WriteLine($"[usb-reset] failed to open {usbDevPath}");
                return false;
            }

            try
            {
                var result = ioctl(fd, USBDEVFS_RESET, 0);
                if (result < 0)
                {
                    Console.WriteLine($"[usb-reset] ioctl USBDEVFS_RESET failed: {result}");
                    return false;
                }

                Console.WriteLine("[usb-reset] ioctl reset succeeded");
                Thread.Sleep(800);
                return true;
            }
            finally
            {
                close(fd);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[usb-reset] ioctl reset failed: {ex.Message}");
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, int arg);
}
