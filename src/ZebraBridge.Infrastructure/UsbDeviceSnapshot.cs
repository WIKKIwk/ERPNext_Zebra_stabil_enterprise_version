using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public static class UsbDeviceSnapshot
{
    public static IReadOnlyList<Dictionary<string, object?>> BuildPayload(PrinterOptions options)
    {
        var status = PrinterPresenceProbe.GetStatus(options);
        if (status.VendorId == 0 && status.ProductId == 0)
        {
            return Array.Empty<Dictionary<string, object?>>();
        }

        var product = string.IsNullOrWhiteSpace(options.PrinterName)
            ? "RFID Printer"
            : options.PrinterName!.Trim();

        var payload = new Dictionary<string, object?>
        {
            ["vendor_id"] = status.VendorId,
            ["product_id"] = status.ProductId,
            ["manufacturer"] = "Zebra",
            ["product"] = product,
            ["bus"] = null,
            ["address"] = null,
            ["connected"] = status.Connected
        };

        return new List<Dictionary<string, object?>> { payload };
    }
}
