using System.Text;
using ZebraBridge.Core;

namespace ZebraBridge.Application;

public interface IPrinterControlService
{
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class PrinterControlService : IPrinterControlService
{
    private readonly PrinterOptions _printerOptions;
    private readonly IPrinterTransportFactory _transportFactory;
    private readonly PrintCoordinator _printCoordinator;

    public PrinterControlService(
        PrinterOptions printerOptions,
        IPrinterTransportFactory transportFactory,
        PrintCoordinator printCoordinator)
    {
        _printerOptions = printerOptions;
        _transportFactory = transportFactory;
        _printCoordinator = printCoordinator;
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var zpl = ZplBuilder.BuildResumePrinting(_printerOptions.ZplEol);
        await SendAsync(zpl, cancellationToken);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var zpl = ZplBuilder.BuildResetPrinter(_printerOptions.ZplEol)
                  + ZplBuilder.BuildResumePrinting(_printerOptions.ZplEol);
        await SendAsync(zpl, cancellationToken);
    }

    private async Task SendAsync(string zpl, CancellationToken cancellationToken)
    {
        await _printCoordinator.RunLockedAsync(async () =>
        {
            var transport = _transportFactory.Create();
            await transport.SendAsync(Encoding.ASCII.GetBytes(zpl), cancellationToken);
            return 0;
        }, cancellationToken);
    }
}
