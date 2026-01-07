namespace ZebraBridge.Core;

public static class ZplBuilder
{
    public static string BuildFeedOneLabel(string eol) => string.Concat("~PH", eol);

    public static string BuildResumePrinting(string eol) => string.Concat("~PS", eol);

    public static string BuildResetPrinter(string eol) => string.Concat("~JA", eol);

    public static string BuildEncodeCommandStream(string epcHex, RfidWriteOptions options, string eol)
    {
        var stream = BuildResumePrinting(eol) + BuildRfidWrite(epcHex, options, eol);

        if (options.FeedAfterEncode && !options.PrintHumanReadable)
        {
            stream += BuildFeedOneLabel(eol);
        }

        return stream;
    }

    public static string BuildRfidSetup(RfidWriteOptions options)
    {
        var action = (options.ErrorHandlingAction ?? "N").Trim().ToUpperInvariant();
        if (action is not ("N" or "P" or "E"))
        {
            throw new ZebraBridgeException("RFID error handling action must be N, P, or E.");
        }

        if (options.LabelsToTryOnError < 1 || options.LabelsToTryOnError > 10)
        {
            throw new ZebraBridgeException("LabelsToTryOnError must be between 1 and 10.");
        }

        return $"^RS{options.TagType},,,{options.LabelsToTryOnError},{action}";
    }

    public static string BuildRfidWrite(string epcHex, RfidWriteOptions options, string eol)
    {
        var epc = Epc.Normalize(epcHex);
        Epc.Validate(epc);

        var wordCount = Epc.HexToWordCount(epc);
        var copies = Math.Max(options.Copies, 1);

        var lines = new List<string> { "^XA" };

        if (options.FeedAfterEncode && !options.PrintHumanReadable)
        {
            lines.Add("^MMT");
        }

        lines.Add(BuildRfidSetup(options));

        if (options.MemoryBank == 1 && options.WordPointer == 2 && options.AutoAdjustPcBits)
        {
            lines.Add($"^RFW,H,,,A^FD{epc}^FS");
        }
        else
        {
            var byteCount = wordCount * 2;
            lines.Add($"^RFW,H,{options.WordPointer},{byteCount},{options.MemoryBank}^FD{epc}^FS");
        }

        if (options.PrintHumanReadable)
        {
            lines.Add("^FO50,50^A0N,30,30");
            lines.Add($"^FD{epc}^FS");
        }

        lines.Add($"^PQ{copies}");
        lines.Add("^XZ");

        return string.Join(eol, lines) + eol;
    }
}
