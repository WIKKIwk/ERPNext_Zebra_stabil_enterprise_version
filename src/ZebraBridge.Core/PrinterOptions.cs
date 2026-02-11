namespace ZebraBridge.Core;

public sealed class PrinterOptions
{
    public string? DevicePath { get; set; }
    public string? PrinterName { get; set; }
    public string Transport { get; set; } = "device";
    public int VendorId { get; set; } = 0x0A5F;
    public int ProductId { get; set; } = 0x0193;
    public int UsbTimeoutMs { get; set; } = 5000;
    public bool FeedAfterEncode { get; set; } = true;
    public bool ResetBeforeEncode { get; set; } = true;
    public int LabelsToTryOnError { get; set; } = 1;
    public string ErrorHandlingAction { get; set; } = "N";
    public string ZplEol { get; set; } = "\n";
    public string? RfidZplTemplate { get; set; }
    public string? RfidZplTemplatePath { get; set; }

    public void ApplyEnvironment()
    {
        DevicePath = Override(DevicePath, "ZEBRA_DEVICE_PATH");
        PrinterName = Override(PrinterName, "ZEBRA_PRINTER_NAME");
        VendorId = OverrideInt(VendorId, "ZEBRA_VENDOR_ID");
        ProductId = OverrideInt(ProductId, "ZEBRA_PRODUCT_ID");
        UsbTimeoutMs = OverrideInt(UsbTimeoutMs, "ZEBRA_USB_TIMEOUT_MS");
        Transport = Override(Transport, "ZEBRA_TRANSPORT") ?? Transport;
        FeedAfterEncode = OverrideBool(FeedAfterEncode, "ZEBRA_FEED_AFTER_ENCODE");
        ResetBeforeEncode = OverrideBool(ResetBeforeEncode, "ZEBRA_RESET_BEFORE_ENCODE");
        LabelsToTryOnError = OverrideInt(LabelsToTryOnError, "ZEBRA_RFID_LABELS_TO_TRY_ON_ERROR");
        ErrorHandlingAction = Override(ErrorHandlingAction, "ZEBRA_RFID_ERROR_HANDLING_ACTION") ?? ErrorHandlingAction;
        ZplEol = Override(ZplEol, "ZEBRA_ZPL_EOL") ?? ZplEol;
        RfidZplTemplate = Override(RfidZplTemplate, "ZEBRA_RFID_ZPL_TEMPLATE");
        RfidZplTemplatePath = Override(RfidZplTemplatePath, "ZEBRA_RFID_ZPL_TEMPLATE_PATH");

        if (!string.IsNullOrWhiteSpace(RfidZplTemplatePath))
        {
            var path = RfidZplTemplatePath.Trim();
            if (!File.Exists(path))
            {
                throw new ZebraBridgeException($"RFID ZPL template file not found: {path}");
            }
            RfidZplTemplate = File.ReadAllText(path);
        }
    }

    private static string? Override(string? current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        return string.IsNullOrWhiteSpace(raw) ? current : raw.Trim();
    }

    private static int OverrideInt(int current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return current;
        }
        if (int.TryParse(raw.Trim(), out var parsed))
        {
            return parsed;
        }
        if (raw.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(raw.Trim()[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            return hex;
        }
        return current;
    }

    private static bool OverrideBool(bool current, string envKey)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return current;
        }
        var s = raw.Trim().ToLowerInvariant();
        if (s is "1" or "true" or "yes" or "y" or "on")
        {
            return true;
        }
        if (s is "0" or "false" or "no" or "n" or "off")
        {
            return false;
        }
        return current;
    }
}
