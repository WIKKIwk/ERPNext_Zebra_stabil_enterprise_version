using System.Text;
using ZebraBridge.Core;

namespace ZebraBridge.Application;

public sealed record EncodeRequest(
    string Epc,
    int Copies = 1,
    bool PrintHumanReadable = false,
    bool FeedAfterEncode = true,
    bool DryRun = false,
    IReadOnlyDictionary<string, string>? LabelFields = null
);

public sealed record EncodeResult(bool Ok, string Message, string? EpcHex = null, string? Zpl = null);

public enum EncodeBatchMode
{
    Manual,
    Auto
}

public sealed record EncodeBatchItem(string Epc, int Copies = 1);

public sealed record EncodeBatchRequest(
    EncodeBatchMode Mode,
    IReadOnlyList<EncodeBatchItem>? Items = null,
    int AutoCount = 1,
    bool PrintHumanReadable = false,
    bool FeedAfterEncode = true,
    IReadOnlyDictionary<string, string>? LabelFields = null
);

public sealed record EncodeBatchItemResult(string EpcHex, int Copies, bool Ok, string Message);

public sealed record EncodeBatchResult(
    bool Ok,
    int TotalLabelsRequested,
    int TotalLabelsSucceeded,
    int UniqueEpcsSucceeded,
    IReadOnlyList<EncodeBatchItemResult> Items
);

public sealed record TransceiveRequest(string Zpl, int ReadTimeoutMs = 2000, int MaxBytes = 32768);

public sealed record TransceiveResult(bool Ok, string Message, string Output, int OutputBytes);

public interface IEncodeService
{
    Task<EncodeResult> EncodeAsync(EncodeRequest request, CancellationToken cancellationToken = default);
    Task<EncodeBatchResult> EncodeBatchAsync(EncodeBatchRequest request, CancellationToken cancellationToken = default);
    Task<TransceiveResult> TransceiveAsync(TransceiveRequest request, CancellationToken cancellationToken = default);
}

public sealed class EncodeService : IEncodeService
{
    private readonly PrinterOptions _printerOptions;
    private readonly IPrinterTransportFactory _transportFactory;
    private readonly PrintCoordinator _printCoordinator;
    private readonly IEpcGenerator _epcGenerator;

    public EncodeService(
        PrinterOptions printerOptions,
        IPrinterTransportFactory transportFactory,
        PrintCoordinator printCoordinator,
        IEpcGenerator epcGenerator)
    {
        _printerOptions = printerOptions;
        _transportFactory = transportFactory;
        _printCoordinator = printCoordinator;
        _epcGenerator = epcGenerator;
    }

    public async Task<EncodeResult> EncodeAsync(EncodeRequest request, CancellationToken cancellationToken = default)
    {
        var epcHex = Epc.Normalize(request.Epc);
        Epc.Validate(epcHex);

        var options = BuildOptions(request.PrintHumanReadable, request.Copies, request.FeedAfterEncode);
        var zpl = ZplBuilder.BuildEncodeCommandStream(
            epcHex,
            options,
            _printerOptions.ZplEol,
            _printerOptions.RfidZplTemplate,
            request.LabelFields);

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

    public async Task<EncodeBatchResult> EncodeBatchAsync(
        EncodeBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var items = NormalizeBatchItems(request);
        var totalRequested = items.Sum(item => item.Copies);
        var results = new List<EncodeBatchItemResult>();
        var succeeded = 0;

        await _printCoordinator.RunLockedAsync(async () =>
        {
            var transport = _transportFactory.Create();
            var reset = ZplBuilder.BuildResetPrinter(_printerOptions.ZplEol)
                        + ZplBuilder.BuildResumePrinting(_printerOptions.ZplEol);

            await transport.SendAsync(Encoding.ASCII.GetBytes(reset), cancellationToken);

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                try
                {
                    var options = BuildOptions(request.PrintHumanReadable, item.Copies, request.FeedAfterEncode);
                    var zpl = ZplBuilder.BuildEncodeCommandStream(
                        item.Epc,
                        options,
                        _printerOptions.ZplEol,
                        _printerOptions.RfidZplTemplate,
                        request.LabelFields);

                    await transport.SendAsync(Encoding.ASCII.GetBytes(zpl), cancellationToken);

                    succeeded += item.Copies;
                    results.Add(new EncodeBatchItemResult(item.Epc, item.Copies, true, "Sent."));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ZebraBridgeException ex)
                {
                    results.Add(new EncodeBatchItemResult(item.Epc, item.Copies, false, ex.Message));
                    AppendSkippedItems(items, index + 1, results);
                    break;
                }
                catch (Exception ex)
                {
                    results.Add(new EncodeBatchItemResult(item.Epc, item.Copies, false, ex.Message));
                    AppendSkippedItems(items, index + 1, results);
                    break;
                }
            }

            return 0;
        }, cancellationToken);

        return new EncodeBatchResult(
            succeeded == totalRequested,
            totalRequested,
            succeeded,
            results.Count(item => item.Ok),
            results
        );
    }

    public async Task<TransceiveResult> TransceiveAsync(
        TransceiveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Zpl))
        {
            throw new ZebraBridgeException("ZPL is required.");
        }

        var readTimeoutMs = Math.Clamp(request.ReadTimeoutMs, 50, 20000);
        var maxBytes = Math.Clamp(request.MaxBytes, 1, 262144);

        var zpl = request.Zpl;
        if (!zpl.EndsWith(_printerOptions.ZplEol, StringComparison.Ordinal))
        {
            zpl += _printerOptions.ZplEol;
        }

        var output = await _printCoordinator.RunLockedAsync(async () =>
        {
            var transport = _transportFactory.Create();
            if (transport is not IPrinterTransceiver transceiver)
            {
                throw new PrinterUnsupportedOperationException("Transceive is not supported by the current transport.");
            }

            return await transceiver.TransceiveAsync(
                Encoding.ASCII.GetBytes(zpl),
                readTimeoutMs,
                maxBytes,
                cancellationToken);
        }, cancellationToken);

        var text = Encoding.UTF8.GetString(output);
        var message = output.Length > 0 ? "OK" : "No response from printer.";
        return new TransceiveResult(true, message, text, output.Length);
    }

    private RfidWriteOptions BuildOptions(bool printHumanReadable, int copies, bool feedAfterEncode)
    {
        var normalizedCopies = Math.Clamp(copies, 1, 1000);
        return new RfidWriteOptions(
            PrintHumanReadable: printHumanReadable,
            Copies: normalizedCopies,
            FeedAfterEncode: feedAfterEncode,
            LabelsToTryOnError: _printerOptions.LabelsToTryOnError,
            ErrorHandlingAction: _printerOptions.ErrorHandlingAction
        );
    }

    private List<EncodeBatchItem> NormalizeBatchItems(EncodeBatchRequest request)
    {
        if (request.Mode == EncodeBatchMode.Auto)
        {
            if (request.AutoCount < 1 || request.AutoCount > 1000)
            {
                throw new ArgumentException("AutoCount must be between 1 and 1000.");
            }

            var epcs = _epcGenerator.NextEpcs(request.AutoCount);
            var autoItems = new List<EncodeBatchItem>(epcs.Count);
            foreach (var epc in epcs)
            {
                var epcHex = Epc.Normalize(epc);
                Epc.Validate(epcHex);
                autoItems.Add(new EncodeBatchItem(epcHex, 1));
            }

            return autoItems;
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ZebraBridgeException("Items are required for manual batch mode.");
        }

        var items = new List<EncodeBatchItem>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var epcHex = Epc.Normalize(item.Epc);
            Epc.Validate(epcHex);
            var copies = Math.Clamp(item.Copies, 1, 1000);
            items.Add(new EncodeBatchItem(epcHex, copies));
        }

        return items;
    }

    private static void AppendSkippedItems(
        List<EncodeBatchItem> items,
        int startIndex,
        List<EncodeBatchItemResult> results)
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            var item = items[index];
            results.Add(new EncodeBatchItemResult(
                item.Epc,
                item.Copies,
                false,
                "Skipped due to previous error."));
        }
    }
}
