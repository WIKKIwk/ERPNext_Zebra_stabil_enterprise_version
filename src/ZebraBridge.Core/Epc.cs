using System.Text.RegularExpressions;

namespace ZebraBridge.Core;

public static class Epc
{
    private static readonly Regex HexOnlyRegex = new("^[0-9A-F]+$", RegexOptions.Compiled);

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var candidate = raw.Trim();
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[2..];
        }

        candidate = candidate.Replace(" ", string.Empty).Replace("-", string.Empty);
        return candidate.ToUpperInvariant();
    }

    public static void Validate(string epc)
    {
        if (string.IsNullOrWhiteSpace(epc))
        {
            throw new InvalidEpcException("EPC is required.");
        }

        if (!HexOnlyRegex.IsMatch(epc))
        {
            throw new InvalidEpcException("EPC must be hexadecimal (0-9, A-F).");
        }

        if (epc.Length % 2 != 0)
        {
            throw new InvalidEpcException("EPC hex length must be even (full bytes).");
        }

        if (epc.Length % 4 != 0)
        {
            throw new InvalidEpcException("EPC hex length must be a multiple of 4 (16-bit words).");
        }
    }

    public static int HexToWordCount(string epc)
    {
        Validate(epc);
        return epc.Length / 4;
    }
}
