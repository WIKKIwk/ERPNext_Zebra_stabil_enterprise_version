using System.Net.Http;
using System.Text;
using ZebraBridge.Application;
using ZebraBridge.Core;
using ZebraBridge.Infrastructure;
using ZebraBridge.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);

var corsOrigins = GetCorsOrigins();
var corsAllowAll = IsTrue(Environment.GetEnvironmentVariable("ZEBRA_CORS_ALLOW_ALL"));
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsAllowAll || corsOrigins.Count == 0)
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            policy.WithOrigins(corsOrigins.ToArray());
        }
        policy.AllowAnyHeader();
        policy.AllowAnyMethod();
    });
});

var httpTimeoutMs = GetEnvInt("ZEBRA_HTTP_TIMEOUT_MS", 5000, 500, 60000);
var httpConnectTimeoutMs = GetEnvInt("ZEBRA_HTTP_CONNECT_TIMEOUT_MS", 2000, 200, 30000);

builder.Services.AddSingleton(BuildPrinterOptions(builder.Configuration));
builder.Services.AddSingleton(BuildScaleOptions(builder.Configuration));
builder.Services.AddSingleton(BuildErpAgentOptions(builder.Configuration));
builder.Services.AddSingleton(BuildEpcGeneratorOptions(builder.Configuration));
builder.Services.AddHttpClient("zebra")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(httpConnectTimeoutMs),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    });

builder.Services.AddSingleton<IScaleState, ScaleState>();
builder.Services.AddSingleton<PrintCoordinator>();
builder.Services.AddSingleton<IPrinterTransportFactory, PrinterTransportFactory>();
builder.Services.AddSingleton<IEpcGenerator, FileEpcGenerator>();
builder.Services.AddSingleton<IEncodeService, EncodeService>();
builder.Services.AddSingleton<IPrinterControlService, PrinterControlService>();
builder.Services.AddHostedService<ScaleReaderService>();
builder.Services.AddHostedService<ScaleAutoPrintService>();
builder.Services.AddHostedService<ErpAgentService>();

var app = builder.Build();

app.UseCors();

var apiToken = GetApiToken();
if (!string.IsNullOrWhiteSpace(apiToken))
{
    app.Use(async (context, next) =>
    {
        if (!IsProtectedPath(context.Request.Path) ||
            context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (IsHealthPath(context.Request.Path))
        {
            await next();
            return;
        }

        if (!IsAuthorized(context, apiToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { ok = false, message = "Unauthorized" });
            return;
        }

        await next();
    });
}

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
    transport = printer.Transport,
    transceive_supported = string.Equals(printer.Transport, "usb", StringComparison.OrdinalIgnoreCase)
}));

app.MapGet("/api/v1/usb-devices", (PrinterOptions printer) =>
{
    return Results.Ok(UsbDeviceSnapshot.BuildPayload(printer));
});

app.MapGet("/api/v1/printer/status", (PrinterOptions printer) =>
{
    var status = PrinterPresenceProbe.GetStatus(printer);
    return Results.Ok(new
    {
        connected = status.Connected,
        transport = status.Transport,
        device_path = status.DevicePath ?? string.Empty,
        vendor_id = $"0x{status.VendorId:X4}",
        product_id = $"0x{status.ProductId:X4}",
        message = status.Message
    });
});

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

app.MapPost("/v1/print-jobs", async (
    PrintJobRequestDto request,
    PrinterOptions printer,
    IPrinterTransportFactory transportFactory,
    PrintCoordinator coordinator,
    CancellationToken token) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(request.Zpl))
        {
            return Results.BadRequest(new PrintJobResponseDto(false, "ZPL is required."));
        }

        var copies = Math.Clamp(request.Copies, 1, 1000);
        var job = EnsureEndsWithEol(request.Zpl, printer.ZplEol);

        await coordinator.RunLockedAsync(async () =>
        {
            var transport = transportFactory.Create();
            var payload = Encoding.ASCII.GetBytes(job);
            for (var i = 0; i < copies; i++)
            {
                await transport.SendAsync(payload, token);
            }
            return 0;
        }, token);

        return Results.Ok(new PrintJobResponseDto(true, "Print job sent."));
    }
    catch (PrinterNotFoundException ex)
    {
        return Results.NotFound(new PrintJobResponseDto(false, ex.Message));
    }
    catch (PrinterCommunicationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
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

static bool IsTrue(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return false;
    }
    return raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "on";
}

static int GetEnvInt(string key, int fallback, int min, int max)
{
    var raw = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw.Trim(), out var parsed))
    {
        return fallback;
    }

    if (parsed < min)
    {
        return min;
    }
    return parsed > max ? max : parsed;
}

static string? GetApiToken()
{
    var raw = Environment.GetEnvironmentVariable("ZEBRA_API_TOKEN")
              ?? Environment.GetEnvironmentVariable("ZEBRA_API_AUTH");
    return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}

static bool IsProtectedPath(PathString path)
{
    return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase);
}

static bool IsHealthPath(PathString path)
{
    return path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase);
}

static bool IsAuthorized(HttpContext context, string token)
{
    var header = context.Request.Headers.Authorization.ToString().Trim();
    if (!string.IsNullOrWhiteSpace(header))
    {
        var normalized = NormalizeAuthHeader(header);
        if (string.Equals(normalized, token, StringComparison.Ordinal))
        {
            return true;
        }
    }

    if (context.Request.Headers.TryGetValue("X-Zebra-Token", out var zebraToken) &&
        string.Equals(zebraToken.ToString().Trim(), token, StringComparison.Ordinal))
    {
        return true;
    }

    if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) &&
        string.Equals(apiKey.ToString().Trim(), token, StringComparison.Ordinal))
    {
        return true;
    }

    return false;
}

static string NormalizeAuthHeader(string raw)
{
    var value = raw.Trim();
    if (value.StartsWith("token ", StringComparison.OrdinalIgnoreCase))
    {
        return value[6..].Trim();
    }
    if (value.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return value[7..].Trim();
    }
    return value;
}

static List<string> GetCorsOrigins()
{
    var raw = Environment.GetEnvironmentVariable("ZEBRA_CORS_ORIGINS") ?? string.Empty;
    var entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (entries.Length > 0)
    {
        return entries.Where(origin => !string.IsNullOrWhiteSpace(origin)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    return new List<string>
    {
        "http://127.0.0.1:8000",
        "http://localhost:8000"
    };
}

static string EnsureEndsWithEol(string zpl, string eol)
{
    if (string.IsNullOrWhiteSpace(eol))
    {
        return zpl;
    }
    return zpl.EndsWith(eol, StringComparison.Ordinal) ? zpl : zpl + eol;
}
