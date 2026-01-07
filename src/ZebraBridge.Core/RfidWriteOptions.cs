namespace ZebraBridge.Core;

public sealed record RfidWriteOptions(
    int TagType = 8,
    int MemoryBank = 1,
    int WordPointer = 2,
    bool PrintHumanReadable = false,
    int Copies = 1,
    bool FeedAfterEncode = true,
    bool AutoAdjustPcBits = true,
    int LabelsToTryOnError = 1,
    string ErrorHandlingAction = "N"
);
