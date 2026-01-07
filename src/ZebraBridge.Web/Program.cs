using ZebraBridge.Application;
using ZebraBridge.Core;
using ZebraBridge.Infrastructure;
using ZebraBridge.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(BuildPrinterOptions(builder.Configuration));
builder.Services.AddSingleton(BuildScaleOptions(builder.Configuration));
builder.Services.AddSingleton(BuildErpAgentOptions(builder.Configuration));
builder.Services.AddSingleton(BuildEpcGeneratorOptions(builder.Configuration));
builder.Services.AddHttpClient();

builder.Services.AddSingleton<IScaleState, ScaleState>();
builder.Services.AddSingleton<PrintCoordinator>();
builder.Services.AddSingleton<IPrinterTransportFactory, PrinterTransportFactory>();
builder.Services.AddSingleton<IEpcGenerator, FileEpcGenerator>();
builder.Services.AddSingleton<IEncodeService, EncodeService>();
builder.Services.AddSingleton<IPrinterControlService, PrinterControlService>();
builder.Services.AddHostedService<ScaleReaderService>();
builder.Services.AddHostedService<ErpAgentService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/v1/health", () => Results.Ok(new
{
    ok = true,
    service = "zebra-bridge-v1",
    version = "0.1.0"
}));

app.MapGet("/api/v1/config", (PrinterOptions printer) => Results.Ok(new
{
    device_path = printer.DevicePath ?? string.Empty,
    feed_after_encode = printer.FeedAfterEncode,
    zebra_template_enabled = !string.IsNullOrWhiteSpace(printer.RfidZplTemplate),
    transport = printer.Transport
}));

app.MapGet("/api/v1/scale", (IScaleState scaleState) => Results.Ok(scaleState.Latest));

app.MapGet("/api/v1/scale/ports", () => Results.Ok(ScalePortEnumerator.ListPorts()));

app.MapPost("/api/v1/encode", async (EncodeRequestDto request, PrinterOptions printer, IEncodeService service) =>
{
    try
    {
        var feedAfterEncode = request.FeedAfterEncode ?? printer.FeedAfterEncode;
        var result = await service.EncodeAsync(request.ToServiceRequest(feedAfterEncode));
        return Results.Ok(new EncodeResponseDto(result.Ok, result.Message, result.EpcHex, result.Zpl));
    }
    catch (InvalidEpcException ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/v1/encode-batch", async (EncodeBatchRequestDto request, PrinterOptions printer, IEncodeService service) =>
{
    try
    {
        var feedAfterEncode = request.FeedAfterEncode ?? printer.FeedAfterEncode;
        var result = await service.EncodeBatchAsync(request.ToServiceRequest(feedAfterEncode));
        return Results.Ok(new EncodeBatchResponseDto(
            result.Ok,
            result.TotalLabelsRequested,
            result.TotalLabelsSucceeded,
            result.UniqueEpcsSucceeded,
            result.Items
                .Select(item => new EncodeBatchItemResultDto(item.EpcHex, item.Copies, item.Ok, item.Message))
                .ToList()
        ));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
    catch (InvalidEpcException ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
    catch (PrinterNotFoundException ex)
    {
        return Results.NotFound(new { ok = false, message = ex.Message });
    }
    catch (PrinterCommunicationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (EpcGeneratorException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (PrinterUnsupportedOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
    }
    catch (NotSupportedException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
    }
    catch (ZebraBridgeException ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/v1/transceive", async (TransceiveRequestDto request, IEncodeService service) =>
{
    try
    {
        var result = await service.TransceiveAsync(request.ToServiceRequest());
        return Results.Ok(new TransceiveResponseDto(result.Ok, result.Message, result.Output, result.OutputBytes));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
    catch (PrinterNotFoundException ex)
    {
        return Results.NotFound(new { ok = false, message = ex.Message });
    }
    catch (PrinterCommunicationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (PrinterUnsupportedOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
    }
    catch (NotSupportedException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
    }
    catch (ZebraBridgeException ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/v1/printer/resume", async (IPrinterControlService service) =>
{
    try
    {
        await service.ResumeAsync();
        return Results.Ok(new { ok = true, message = "Printer resumed." });
    }
    catch (PrinterNotFoundException ex)
    {
        return Results.NotFound(new { ok = false, message = ex.Message });
    }
    catch (PrinterCommunicationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/printer/reset", async (IPrinterControlService service) =>
{
    try
    {
        await service.ResetAsync();
        return Results.Ok(new { ok = true, message = "Printer reset." });
    }
    catch (PrinterNotFoundException ex)
    {
        return Results.NotFound(new { ok = false, message = ex.Message });
    }
    catch (PrinterCommunicationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

static PrinterOptions BuildPrinterOptions(IConfiguration config)
{
    var options = new PrinterOptions();
    config.GetSection("Printer").Bind(options);
    options.ApplyEnvironment();
    return options;
}

static ScaleOptions BuildScaleOptions(IConfiguration config)
{
    var options = new ScaleOptions();
    config.GetSection("Scale").Bind(options);
    options.ApplyEnvironment();
    return options;
}

static ErpAgentOptions BuildErpAgentOptions(IConfiguration config)
{
    var options = new ErpAgentOptions();
    config.GetSection("ErpAgent").Bind(options);
    options.ApplyEnvironment();
    return options;
}

static EpcGeneratorOptions BuildEpcGeneratorOptions(IConfiguration config)
{
    var options = new EpcGeneratorOptions();
    config.GetSection("EpcGenerator").Bind(options);
    options.ApplyEnvironment();
    return options;
}
