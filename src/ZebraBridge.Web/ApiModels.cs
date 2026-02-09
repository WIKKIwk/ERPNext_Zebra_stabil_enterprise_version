using ZebraBridge.Application;

namespace ZebraBridge.Web;

public sealed record EncodeRequestDto(
    string Epc,
    int Copies = 1,
    bool PrintHumanReadable = false,
    bool? FeedAfterEncode = null,
    bool DryRun = false,
    IReadOnlyDictionary<string, string>? LabelFields = null
)
{
    public EncodeRequest ToServiceRequest(bool feedAfterEncode)
    {
        return new EncodeRequest(Epc, Copies, PrintHumanReadable, feedAfterEncode, DryRun, LabelFields);
    }
}

public sealed record EncodeResponseDto(bool Ok, string Message, string? EpcHex, string? Zpl);

public sealed record EncodeBatchItemDto(string Epc, int Copies = 1);

public sealed record EncodeBatchRequestDto(
    string Mode = "manual",
    IReadOnlyList<EncodeBatchItemDto>? Items = null,
    int AutoCount = 1,
    bool PrintHumanReadable = false,
    bool? FeedAfterEncode = null,
    IReadOnlyDictionary<string, string>? LabelFields = null
)
{
    public EncodeBatchRequest ToServiceRequest(bool feedAfterEncode)
    {
        return new EncodeBatchRequest(
            ParseMode(Mode),
            Items?.Select(item => new EncodeBatchItem(item.Epc, item.Copies)).ToList(),
            AutoCount,
            PrintHumanReadable,
            feedAfterEncode,
            LabelFields);
    }

    private static EncodeBatchMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return EncodeBatchMode.Manual;
        }

        if (Enum.TryParse<EncodeBatchMode>(mode, true, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException("Mode must be 'manual' or 'auto'.");
    }
}

public sealed record EncodeBatchItemResultDto(string EpcHex, int Copies, bool Ok, string Message);

public sealed record EncodeBatchResponseDto(
    bool Ok,
    int TotalLabelsRequested,
    int TotalLabelsSucceeded,
    int UniqueEpcsSucceeded,
    IReadOnlyList<EncodeBatchItemResultDto> Items
);

public sealed record TransceiveRequestDto(string Zpl, int ReadTimeoutMs = 2000, int MaxBytes = 32768)
{
    public TransceiveRequest ToServiceRequest() => new(Zpl, ReadTimeoutMs, MaxBytes);
}

public sealed record TransceiveResponseDto(bool Ok, string Message, string Output, int OutputBytes);

public sealed record PrintJobRequestDto(string Zpl, int Copies = 1);

public sealed record PrintJobResponseDto(bool Ok, string Message);
