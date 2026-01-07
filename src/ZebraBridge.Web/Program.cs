using ZebraBridge.Application;
using ZebraBridge.Core;
using ZebraBridge.Infrastructure;
using ZebraBridge.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(BuildPrinterOptions(builder.Configuration));
builder.Services.AddSingleton(BuildScaleOptions(builder.Configuration));
builder.Services.AddSingleton(BuildErpAgentOptions(builder.Configuration));

builder.Services.AddSingleton<IScaleState, ScaleState>();
builder.Services.AddSingleton<PrintCoordinator>();
builder.Services.AddSingleton<IPrinterTransportFactory, PrinterTransportFactory>();
builder.Services.AddSingleton<IEncodeService, EncodeService>();

var app = builder.Build();

app.MapGet("/api/v1/health", () => Results.Ok(new
{
    ok = true,
    service = "zebra-bridge-v1",
    version = "0.1.0"
}));

app.MapGet("/api/v1/config", (PrinterOptions printer) => Results.Ok(new
{
    device_path = printer.DevicePath ?? string.Empty,
    feed_after_encode = printer.FeedAfterEncode
}));

app.MapGet("/api/v1/scale", (IScaleState scaleState) => Results.Ok(scaleState.Latest));

app.MapGet("/api/v1/scale/ports", () => Results.Ok(Array.Empty<object>()));

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

app.MapPost("/api/v1/encode-batch", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
app.MapPost("/api/v1/transceive", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

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
