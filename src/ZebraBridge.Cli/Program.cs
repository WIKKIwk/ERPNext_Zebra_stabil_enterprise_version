using System.Text.Json;
using ZebraBridge.Application;
using ZebraBridge.Core;
using ZebraBridge.Infrastructure;

var argsList = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (argsList.Length == 0 || HasHelpFlag(argsList))
{
    PrintUsage();
    return 0;
}

var command = argsList[0].Trim().ToLowerInvariant();
var parser = new ArgParser(argsList.Skip(1));

try
{
    var context = BuildContext();
    return command switch
    {
        "encode" => await HandleEncodeAsync(context, parser),
        "encode-batch" => await HandleEncodeBatchAsync(context, parser),
        "transceive" => await HandleTransceiveAsync(context, parser),
        "printer" => await HandlePrinterAsync(context, parser),
        "tui" => await HandleTuiAsync(parser),
        "config" => HandleConfig(context),
        "version" => HandleVersion(),
        _ => HandleUnknown(command)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static CliContext BuildContext()
{
    var printerOptions = new PrinterOptions();
    printerOptions.ApplyEnvironment();

    var epcOptions = new EpcGeneratorOptions();
    epcOptions.ApplyEnvironment();

    var transportFactory = new PrinterTransportFactory(printerOptions);
    var coordinator = new PrintCoordinator();
    var epcGenerator = new FileEpcGenerator(epcOptions);
    var encodeService = new EncodeService(printerOptions, transportFactory, coordinator, epcGenerator);
    var printerControl = new PrinterControlService(printerOptions, transportFactory, coordinator);

    return new CliContext(printerOptions, encodeService, printerControl);
}

static async Task<int> HandleEncodeAsync(CliContext ctx, ArgParser parser)
{
    var epc = parser.GetString("epc") ?? parser.GetPositional(0);
    if (string.IsNullOrWhiteSpace(epc))
    {
        Console.Error.WriteLine("Error: --epc is required.");
        return 1;
    }

    var copies = parser.GetInt("copies", 1, 1, 1000);
    var printHuman = parser.GetBool("print-human", parser.GetBool("human", false));
    var feedAfter = parser.GetBool("feed", true);
    if (parser.HasFlag("no-feed"))
    {
        feedAfter = false;
    }
    var dryRun = parser.GetBool("dry-run", false);

    var result = await ctx.EncodeService.EncodeAsync(
        new EncodeRequest(epc, copies, printHuman, feedAfter, dryRun));

    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
    return result.Ok ? 0 : 1;
}

static async Task<int> HandleEncodeBatchAsync(CliContext ctx, ArgParser parser)
{
    var modeRaw = parser.GetString("mode", "manual").Trim().ToLowerInvariant();
    var mode = modeRaw == "auto" ? EncodeBatchMode.Auto : EncodeBatchMode.Manual;
    var printHuman = parser.GetBool("print-human", parser.GetBool("human", false));
    var feedAfter = parser.GetBool("feed", true);
    if (parser.HasFlag("no-feed"))
    {
        feedAfter = false;
    }
    var autoCount = parser.GetInt("count", 1, 1, 1000);
    var items = new List<EncodeBatchItem>();

    if (mode == EncodeBatchMode.Manual)
    {
        var raw = parser.GetString("items", string.Empty);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            items = ParseItems(raw);
        }

        if (items.Count == 0)
        {
            Console.Error.WriteLine("Error: --items is required for manual mode.");
            return 1;
        }
    }

    var request = new EncodeBatchRequest(mode, items, autoCount, printHuman, feedAfter);
    var result = await ctx.EncodeService.EncodeBatchAsync(request);

    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
    return result.Ok ? 0 : 1;
}

static async Task<int> HandleTransceiveAsync(CliContext ctx, ArgParser parser)
{
    var zpl = parser.GetString("zpl") ?? string.Empty;
    var zplFile = parser.GetString("zpl-file");
    if (!string.IsNullOrWhiteSpace(zplFile))
    {
        if (!File.Exists(zplFile))
        {
            Console.Error.WriteLine($"Error: zpl file not found: {zplFile}");
            return 1;
        }
        zpl = File.ReadAllText(zplFile);
    }

    if (string.IsNullOrWhiteSpace(zpl))
    {
        Console.Error.WriteLine("Error: --zpl or --zpl-file is required.");
        return 1;
    }

    var timeout = parser.GetInt("timeout", 2000, 50, 20000);
    var maxBytes = parser.GetInt("max", 32768, 1, 262144);

    var result = await ctx.EncodeService.TransceiveAsync(new TransceiveRequest(zpl, timeout, maxBytes));
    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
    return result.Ok ? 0 : 1;
}

static async Task<int> HandlePrinterAsync(CliContext ctx, ArgParser parser)
{
    var action = parser.GetPositional(0)?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(action))
    {
        Console.Error.WriteLine("Error: printer command requires 'resume' or 'reset'.");
        return 1;
    }

    if (action == "resume")
    {
        await ctx.PrinterControl.ResumeAsync();
        Console.WriteLine("{\"ok\":true,\"message\":\"Printer resumed.\"}");
        return 0;
    }

    if (action == "reset")
    {
        await ctx.PrinterControl.ResetAsync();
        Console.WriteLine("{\"ok\":true,\"message\":\"Printer reset.\"}");
        return 0;
    }

    Console.Error.WriteLine("Error: printer command requires 'resume' or 'reset'.");
    return 1;
}

static int HandleConfig(CliContext ctx)
{
    var payload = new
    {
        device_path = ctx.PrinterOptions.DevicePath ?? string.Empty,
        vendor_id = $"0x{ctx.PrinterOptions.VendorId:X4}",
        product_id = $"0x{ctx.PrinterOptions.ProductId:X4}",
        usb_timeout_ms = ctx.PrinterOptions.UsbTimeoutMs,
        feed_after_encode = ctx.PrinterOptions.FeedAfterEncode,
        labels_to_try_on_error = ctx.PrinterOptions.LabelsToTryOnError,
        error_handling_action = ctx.PrinterOptions.ErrorHandlingAction,
        template_enabled = !string.IsNullOrWhiteSpace(ctx.PrinterOptions.RfidZplTemplate),
        transport = ctx.PrinterOptions.Transport
    };

    Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    return 0;
}

static int HandleVersion()
{
    Console.WriteLine("zebra-bridge-cli 0.1.0");
    return 0;
}

static async Task<int> HandleTuiAsync(ArgParser parser)
{
    var baseUrl = parser.GetString("url");
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        var host = Environment.GetEnvironmentVariable("ZEBRA_WEB_HOST") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("ZEBRA_WEB_PORT") ?? "18000";
        baseUrl = $"http://{host}:{port}";
    }
    baseUrl = baseUrl.TrimEnd('/');

    using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
    Console.CursorVisible = false;

    var exit = false;
    Console.CancelKeyPress += (_, args) =>
    {
        args.Cancel = true;
        exit = true;
    };

    while (!exit)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
            {
                break;
            }
        }

        var now = DateTimeOffset.Now;
        string healthLine = "Health: unknown";
        string configLine = "Config: unavailable";
        string scaleLine = "Scale: unavailable";

        try
        {
            var health = await GetJsonAsync(client, "/api/v1/health");
            var service = health.TryGetProperty("service", out var svc) ? svc.GetString() : "service";
            var ok = health.TryGetProperty("ok", out var okValue) && okValue.GetBoolean();
            healthLine = $"Health: {(ok ? "OK" : "FAIL")} ({service})";
        }
        catch (Exception ex)
        {
            healthLine = $"Health: ERROR ({ex.Message})";
        }

        try
        {
            var config = await GetJsonAsync(client, "/api/v1/config");
            var device = config.TryGetProperty("device_path", out var dev) ? dev.GetString() : "";
            var transport = config.TryGetProperty("transport", out var tr) ? tr.GetString() : "";
            var template = config.TryGetProperty("zebra_template_enabled", out var te) && te.GetBoolean()
                ? "template=on"
                : "template=off";
            configLine = $"Config: device={device} transport={transport} {template}";
        }
        catch (Exception ex)
        {
            configLine = $"Config: ERROR ({ex.Message})";
        }

        try
        {
            var scale = await GetJsonAsync(client, "/api/v1/scale");
            if (scale.TryGetProperty("weight", out var weightElement) && weightElement.ValueKind == JsonValueKind.Number)
            {
                var weight = weightElement.GetDouble();
                var unit = scale.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() : "kg";
                var stable = scale.TryGetProperty("stable", out var stableElement) && stableElement.ValueKind != JsonValueKind.Null
                    ? (stableElement.GetBoolean() ? "stable" : "unstable")
                    : "unverified";
                var port = scale.TryGetProperty("port", out var portElement) ? portElement.GetString() : "";
                scaleLine = $"Scale: {weight:0.000} {unit} ({stable}) {port}";
            }
            else
            {
                var error = scale.TryGetProperty("error", out var err) ? err.GetString() : "no data";
                scaleLine = $"Scale: {error}";
            }
        }
        catch (Exception ex)
        {
            scaleLine = $"Scale: ERROR ({ex.Message})";
        }

        Console.Clear();
        Console.WriteLine("ZebraBridge TUI");
        Console.WriteLine("Press Q or Esc to quit.");
        Console.WriteLine(new string('-', 48));
        Console.WriteLine($"Time:   {now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Base:   {baseUrl}");
        Console.WriteLine(healthLine);
        Console.WriteLine(configLine);
        Console.WriteLine(scaleLine);

        await Task.Delay(500);
    }

    Console.CursorVisible = true;
    return 0;
}

static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
{
    using var response = await client.GetAsync(path);
    var payload = await response.Content.ReadAsStringAsync();
    response.EnsureSuccessStatusCode();

    using var doc = JsonDocument.Parse(payload);
    return doc.RootElement.Clone();
}

static int HandleUnknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 1;
}

static List<EncodeBatchItem> ParseItems(string raw)
{
    var items = new List<EncodeBatchItem>();
    var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var part in parts)
    {
        var item = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (item.Length == 0)
        {
            continue;
        }
        var epc = item[0];
        if (string.IsNullOrWhiteSpace(epc))
        {
            continue;
        }
        var copies = 1;
        if (item.Length > 1 && int.TryParse(item[1], out var parsed))
        {
            copies = parsed;
        }
        items.Add(new EncodeBatchItem(epc, copies));
    }
    return items;
}

static bool HasHelpFlag(string[] args)
{
    return args.Any(arg => arg is "-h" or "--help" or "help");
}

static void PrintUsage()
{
    Console.WriteLine("ZebraBridge CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  zebra-cli encode --epc <hex> [--copies N] [--print-human] [--feed|--no-feed] [--dry-run]");
    Console.WriteLine("  zebra-cli encode-batch --mode <manual|auto> [--items epc:copies,...] [--count N]");
    Console.WriteLine("  zebra-cli transceive --zpl <ZPL> [--timeout ms] [--max bytes]");
    Console.WriteLine("  zebra-cli transceive --zpl-file path/to/file.zpl [--timeout ms] [--max bytes]");
    Console.WriteLine("  zebra-cli printer resume|reset");
    Console.WriteLine("  zebra-cli tui [--url http://127.0.0.1:18000]");
    Console.WriteLine("  zebra-cli config");
    Console.WriteLine("  zebra-cli version");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  zebra-cli encode --epc 3034257BF7194E4000000001 --copies 2");
    Console.WriteLine("  zebra-cli encode-batch --items 3034AA:1,3034BB:2");
    Console.WriteLine("  zebra-cli encode-batch --mode auto --count 5");
    Console.WriteLine("  zebra-cli transceive --zpl \"^XA^HH^XZ\"");
}

static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

sealed record CliContext(
    PrinterOptions PrinterOptions,
    EncodeService EncodeService,
    PrinterControlService PrinterControl);

sealed class ArgParser
{
    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public ArgParser(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var token = list[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                _positionals.Add(token);
                continue;
            }

            var key = token[2..];
            if (key.Contains('='))
            {
                var parts = key.Split('=', 2);
                _values[parts[0]] = parts[1];
                continue;
            }

            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _values[key] = list[i + 1];
                i += 1;
            }
            else
            {
                _flags.Add(key);
            }
        }
    }

    public string? GetPositional(int index) => index >= 0 && index < _positionals.Count ? _positionals[index] : null;

    public bool HasFlag(string name) => _flags.Contains(name);

    public string? GetString(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string GetString(string name, string fallback) => GetString(name) ?? fallback;

    public bool GetBool(string name, bool fallback)
    {
        if (_flags.Contains(name))
        {
            return true;
        }
        if (!_values.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => fallback
        };
    }

    public int GetInt(string name, int fallback, int min, int max)
    {
        if (!_values.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        if (!int.TryParse(raw.Trim(), out var parsed))
        {
            return fallback;
        }
        if (parsed < min)
        {
            return min;
        }
        return parsed > max ? max : parsed;
    }
}
