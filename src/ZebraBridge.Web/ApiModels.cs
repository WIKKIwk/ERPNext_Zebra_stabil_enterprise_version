using ZebraBridge.Application;

namespace ZebraBridge.Web;

public sealed record EncodeRequestDto(
    string Epc,
    int Copies = 1,
    bool PrintHumanReadable = false,
    bool? FeedAfterEncode = null,
    bool DryRun = false
)
{
    public EncodeRequest ToServiceRequest(bool feedAfterEncode)
    {
        return new EncodeRequest(Epc, Copies, PrintHumanReadable, feedAfterEncode, DryRun);
    }
}

public sealed record EncodeResponseDto(bool Ok, string Message, string? EpcHex, string? Zpl);
