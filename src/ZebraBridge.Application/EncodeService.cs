using System.Text;
using ZebraBridge.Core;

namespace ZebraBridge.Application;

public sealed record EncodeRequest(
    string Epc,
    int Copies = 1,
    bool PrintHumanReadable = false,
    bool FeedAfterEncode = true,
    bool DryRun = false
);

public sealed record EncodeResult(bool Ok, string Message, string? EpcHex = null, string? Zpl = null);

public interface IEncodeService
{
    Task<EncodeResult> EncodeAsync(EncodeRequest request, CancellationToken cancellationToken = default);
}

public sealed class EncodeService : IEncodeService
{
    private readonly PrinterOptions _printerOptions;
    private readonly IPrinterTransportFactory _transportFactory;
    private readonly PrintCoordinator _printCoordinator;

    public EncodeService(
        PrinterOptions printerOptions,
        IPrinterTransportFactory transportFactory,
        PrintCoordinator printCoordinator)
    {
        _printerOptions = printerOptions;
        _transportFactory = transportFactory;
        _printCoordinator = printCoordinator;
    }

    public async Task<EncodeResult> EncodeAsync(EncodeRequest request, CancellationToken cancellationToken = default)
    {
        var epcHex = Epc.Normalize(request.Epc);
        Epc.Validate(epcHex);

        var copies = Math.Clamp(request.Copies, 1, 1000);
        var options = new RfidWriteOptions(
            PrintHumanReadable: request.PrintHumanReadable,
            Copies: copies,
            FeedAfterEncode: request.FeedAfterEncode,
            LabelsToTryOnError: _printerOptions.LabelsToTryOnError,
            ErrorHandlingAction: _printerOptions.ErrorHandlingAction
        );

        var zpl = ZplBuilder.BuildRfidWrite(epcHex, options, _printerOptions.ZplEol);

        if (request.DryRun)
        {
            return new EncodeResult(true, "Dry-run: ZPL generated.", epcHex, zpl);
        }

        await _printCoordinator.RunLockedAsync(async () =>
        {
            var transport = _transportFactory.Create();
            await transport.SendAsync(Encoding.ASCII.GetBytes(zpl), cancellationToken);
            return 0;
        }, cancellationToken);

        return new EncodeResult(true, "EPC written successfully.", epcHex);
    }
}
