using System.Net.Http;
using System.Text;
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
        "setup" => HandleSetup(parser),
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

    Console.WriteLine(ToJson(result));
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

    Console.WriteLine(ToJson(result));
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
    Console.WriteLine(ToJson(result));
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

    Console.WriteLine(ToJson(payload));
    return 0;
}

static int HandleVersion()
{
    Console.WriteLine("zebra-bridge-cli 0.1.0");
    return 0;
}

static int HandleSetup(ArgParser parser)
{
    var mode = parser.GetString("mode")?.Trim().ToLowerInvariant();
    if (parser.HasFlag("offline"))
    {
        mode = "offline";
    }
    else if (parser.HasFlag("online"))
    {
        mode = "online";
    }

    if (string.IsNullOrWhiteSpace(mode))
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Error: --online or --offline is required when input is redirected.");
            return 1;
        }
        return RunSetupWizard(allowSkip: false) ? 0 : 1;
    }

    if (mode == "offline")
    {
        WriteErpConfig(enabled: false, baseUrl: string.Empty, auth: string.Empty, device: string.Empty);
        Console.WriteLine("ERP mode set to OFFLINE.");
        return 0;
    }

    if (mode != "online")
    {
        Console.Error.WriteLine("Invalid mode. Use --online or --offline.");
        return 1;
    }

    var existing = TryLoadErpProfile();
    var erpUrl = parser.GetString("erp-url");
    if (string.IsNullOrWhiteSpace(erpUrl))
    {
        if (!Console.IsInputRedirected)
        {
            erpUrl = PromptWithDefault("ERP URL", existing?.BaseUrl);
        }
        else
        {
            erpUrl = existing?.BaseUrl;
        }
    }
    if (string.IsNullOrWhiteSpace(erpUrl))
    {
        Console.Error.WriteLine("ERP URL is required for online mode.");
        return 1;
    }

    var token = parser.GetString("erp-token");
    if (string.IsNullOrWhiteSpace(token))
    {
        if (!Console.IsInputRedirected)
        {
            var label = string.IsNullOrWhiteSpace(existing?.Auth)
                ? "ERP Token (api_key:api_secret or 'token ...')"
                : "ERP Token (leave empty to keep current)";
            token = PromptSecret($"{label}: ");
            if (string.IsNullOrWhiteSpace(token))
            {
                token = existing?.Auth;
            }
        }
        else
        {
            token = existing?.Auth;
        }
    }
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("ERP Token is required for online mode.");
        return 1;
    }

    var deviceDefault = string.IsNullOrWhiteSpace(existing?.Device) ? Environment.MachineName : existing!.Device;
    var device = parser.GetString("device");
    if (string.IsNullOrWhiteSpace(device))
    {
        if (!Console.IsInputRedirected)
        {
            device = PromptWithDefault("Local device name", deviceDefault);
        }
        else
        {
            device = deviceDefault;
        }
    }

    var normalizedUrl = NormalizeBaseUrl(erpUrl);
    WriteErpConfig(enabled: true, baseUrl: normalizedUrl, auth: token, device: device ?? deviceDefault);
    Console.WriteLine($"ERP mode set to ONLINE. Target: {normalizedUrl}");
    return 0;
}

static async Task<int> HandleTuiAsync(ArgParser parser)
{
    var baseUrl = parser.GetString("url");
    var forceSetup = parser.HasFlag("setup");
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        var host = Environment.GetEnvironmentVariable("ZEBRA_WEB_HOST") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("ZEBRA_WEB_PORT") ?? "18000";
        baseUrl = $"http://{host}:{port}";
    }
    baseUrl = baseUrl.TrimEnd('/');

    if ((forceSetup || !HasErpConfig()) && !Console.IsInputRedirected)
    {
        if (!RunSetupWizard(allowSkip: !forceSetup && HasErpConfig()))
        {
            return 1;
        }
    }

    var httpTimeoutMs = GetEnvInt("ZEBRA_TUI_HTTP_TIMEOUT_MS", 1500, 200, 10000);
    var connectTimeoutMs = GetEnvInt("ZEBRA_TUI_CONNECT_TIMEOUT_MS", 800, 100, 5000);
    using var handler = new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMs)
    };
    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs)
    };
    Console.CursorVisible = false;

    var exit = false;
    Console.CancelKeyPress += (_, args) =>
    {
        args.Cancel = true;
        exit = true;
    };

    var healthLine = "Health: waiting";
    var configLine = "Config: waiting";
    var scaleLine = "Scale: waiting";
    var erpLine = "ERP: waiting";
    var printerState = "SEARCHING";
    var printerDetail = "probe pending";
    var printerConnected = false;
    var healthOk = false;

    var scaleIntervalMs = GetEnvInt("ZEBRA_TUI_SCALE_MS", 100, 20, 1000);
    var refreshIntervalMs = GetEnvInt("ZEBRA_TUI_REFRESH_MS", 50, 20, 500);
    var scaleBoostMs = GetEnvInt("ZEBRA_TUI_SCALE_BOOST_MS", 1200, 200, 5000);
    var scaleBoostPollMs = GetEnvInt("ZEBRA_TUI_SCALE_BOOST_POLL_MS", 50, 20, 200);
    var scaleBoostUntil = 0L;
    var scaleOk = false;
    var scalePort = string.Empty;

    var lastHealthAt = 0L;
    var lastConfigAt = 0L;
    var lastPrinterAt = 0L;
    var lastScaleAt = 0L;
    var lastErpAt = 0L;
    var lastBeatAt = 0L;
    var beatIndex = 0;
    var beatFrames = new[] { ".", "o", "O", "o" };
    var erpHeartbeatMs = GetEnvInt("ZEBRA_TUI_ERP_HEARTBEAT_MS", 10_000, 2000, 60_000);
    var erpTimeoutMs = GetEnvInt("ZEBRA_TUI_ERP_TIMEOUT_MS", 2500, 200, 15000);
    var erpConnectTimeoutMs = GetEnvInt("ZEBRA_TUI_ERP_CONNECT_TIMEOUT_MS", 800, 200, 10000);
    HttpClient? erpClient = null;
    string? erpClientBaseUrl = null;

    while (!exit)
    {
        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
            {
                break;
            }
            if (key.Key is ConsoleKey.S)
            {
                Console.CursorVisible = true;
                var updated = RunSetupWizard(allowSkip: true);
                Console.CursorVisible = false;
                if (!updated)
                {
                    break;
                }
                continue;
            }
        }

        var now = DateTimeOffset.Now;
        var nowMs = now.ToUnixTimeMilliseconds();

        if (nowMs - lastBeatAt >= 120)
        {
            beatIndex = (beatIndex + 1) % beatFrames.Length;
            lastBeatAt = nowMs;
        }

        var scalePollMs = scaleIntervalMs;
        if (scaleBoostUntil > nowMs || !scaleOk)
        {
            scalePollMs = Math.Min(scalePollMs, scaleBoostPollMs);
        }

        if (nowMs - lastHealthAt >= 1000)
        {
            try
            {
                var health = await GetJsonAsync(client, "/api/v1/health");
                var service = health.TryGetProperty("service", out var svc) ? svc.GetString() : "service";
                var ok = health.TryGetProperty("ok", out var okValue) && okValue.GetBoolean();
                healthOk = ok;
                healthLine = $"Health: {(ok ? "OK" : "FAIL")} ({service})";
            }
            catch (Exception ex)
            {
                healthOk = false;
                healthLine = $"Health: ERROR ({ex.Message})";
            }
            lastHealthAt = nowMs;
        }

        if (nowMs - lastConfigAt >= 1000)
        {
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
            lastConfigAt = nowMs;
        }

        if (nowMs - lastPrinterAt >= 500)
        {
            try
            {
                var status = await GetJsonAsync(client, "/api/v1/printer/status");
                printerConnected = status.TryGetProperty("connected", out var conn) && conn.GetBoolean();
                var transport = status.TryGetProperty("transport", out var tr) ? tr.GetString() : "";
                var device = status.TryGetProperty("device_path", out var dev) ? dev.GetString() : "";
                var vendor = status.TryGetProperty("vendor_id", out var vid) ? vid.GetString() : "";
                var product = status.TryGetProperty("product_id", out var pid) ? pid.GetString() : "";
                var message = status.TryGetProperty("message", out var msg) ? msg.GetString() : "";

                printerState = printerConnected ? "CONNECTED" : "SEARCHING";
                printerDetail = transport == "usb"
                    ? $"{transport} {vendor}:{product}"
                    : $"{transport} {device}";
                if (!string.IsNullOrWhiteSpace(message))
                {
                    printerDetail = $"{printerDetail} - {message}";
                }
            }
            catch (Exception ex)
            {
                printerState = "ERROR";
                printerDetail = ex.Message;
                printerConnected = false;
            }
            lastPrinterAt = nowMs;
        }

        if (nowMs - lastScaleAt >= scalePollMs)
        {
            try
            {
                var scale = await GetJsonAsync(client, "/api/v1/scale");
                var ok = scale.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                var port = scale.TryGetProperty("port", out var portElement) ? portElement.GetString() : "";
                port ??= string.Empty;
                if (ok && (!scaleOk || !string.Equals(scalePort, port, StringComparison.OrdinalIgnoreCase)))
                {
                    scaleBoostUntil = nowMs + scaleBoostMs;
                }
                scaleOk = ok;
                scalePort = port;

                if (scale.TryGetProperty("weight", out var weightElement) && weightElement.ValueKind == JsonValueKind.Number)
                {
                    var weight = weightElement.GetDouble();
                    var unit = scale.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() : "kg";
                    var stable = scale.TryGetProperty("stable", out var stableElement) && stableElement.ValueKind != JsonValueKind.Null
                        ? (stableElement.GetBoolean() ? "stable" : "unstable")
                        : "unverified";
                    scaleLine = $"Scale: {weight:0.000} {unit} ({stable}) {port}";
                }
                else
                {
                    var error = scale.TryGetProperty("error", out var err) ? err.GetString() : "";
                    if (ok)
                    {
                        var boostActive = scaleBoostUntil > nowMs;
                        var status = boostActive ? "refresh" : "waiting";
                        var boostPulse = boostActive ? $" {beatFrames[beatIndex]}" : string.Empty;
                        scaleLine = string.IsNullOrWhiteSpace(port)
                            ? $"Scale: connected ({status}){boostPulse}"
                            : $"Scale: connected ({status}){boostPulse} {port}";
                    }
                    else
                    {
                        var message = string.IsNullOrWhiteSpace(error) ? "waiting" : error;
                        scaleLine = $"Scale: {message}";
                    }
                }
            }
            catch (Exception ex)
            {
                scaleLine = $"Scale: ERROR ({ex.Message})";
                scaleOk = false;
                scalePort = string.Empty;
            }
            lastScaleAt = nowMs;
        }

        if (healthOk && nowMs - lastErpAt >= erpHeartbeatMs)
        {
            var target = TryLoadErpHeartbeatTarget();
            if (target is null)
            {
                erpLine = "ERP: OFFLINE (config missing)";
            }
            else
            {
                if (!string.Equals(erpClientBaseUrl, target.BaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    erpClient?.Dispose();
                    erpClient = BuildErpClient(erpTimeoutMs, erpConnectTimeoutMs);
                    erpClientBaseUrl = target.BaseUrl;
                }
                try
                {
                    var error = await SendErpHeartbeatAsync(erpClient!, target, baseUrl);
                    erpLine = string.IsNullOrWhiteSpace(error) ? "ERP: OK" : $"ERP: ERROR ({TrimForTui(error)})";
                }
                catch (Exception ex)
                {
                    erpLine = $"ERP: ERROR ({TrimForTui(ex.Message)})";
                }
            }
            lastErpAt = nowMs;
        }

        var modeLine = GetModeLine();
        var pulse = beatFrames[beatIndex];
        var statusBadge = printerConnected ? "OK" : "WAIT";
        var printerLine = $"Printer: {printerState} [{statusBadge}] pulse:{pulse} ({printerDetail.Trim()})";

        Console.Clear();
        Console.WriteLine("ZebraBridge Terminal UI");
        Console.WriteLine("==============================================");
        Console.WriteLine($"Time   : {now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Base   : {baseUrl}");
        Console.WriteLine($"{modeLine}");
        Console.WriteLine("----------------------------------------------");
        Console.WriteLine(healthLine);
        Console.WriteLine(erpLine);
        Console.WriteLine(configLine);
        Console.WriteLine(printerLine);
        Console.WriteLine(scaleLine);
        Console.WriteLine("----------------------------------------------");
        Console.WriteLine("Keys   : [Q] Quit  [S] Setup");

        await Task.Delay(refreshIntervalMs);
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

static string GetModeLine()
{
    var profile = TryLoadErpProfile();
    if (profile is null)
    {
        return "Mode: OFFLINE (no ERP config)";
    }

    if (!profile.RpcEnabled || !profile.Enabled)
    {
        return "Mode: OFFLINE (ERP disabled)";
    }

    var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? "missing url" : profile.BaseUrl;
    var device = string.IsNullOrWhiteSpace(profile.Device) ? string.Empty : $" device={profile.Device}";
    return $"Mode: ONLINE ({baseUrl}){device}";
}

static ErpHeartbeatTarget? TryLoadErpHeartbeatTarget()
{
    var profile = TryLoadErpProfile();
    if (profile is null || !profile.RpcEnabled || !profile.Enabled)
    {
        return null;
    }

    var baseUrl = NormalizeBaseUrl(profile.BaseUrl);
    var auth = NormalizeAuth(profile.Auth);
    var secretHeader = string.IsNullOrWhiteSpace(profile.Secret) ? auth : profile.Secret.Trim();
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        return null;
    }
    if (string.IsNullOrWhiteSpace(auth) && string.IsNullOrWhiteSpace(secretHeader))
    {
        return null;
    }

    var name = string.IsNullOrWhiteSpace(profile.Name) ? "default" : profile.Name.Trim();
    var machine = Environment.MachineName;
    var agentId = string.IsNullOrWhiteSpace(profile.AgentId) ? $"zebra-{machine}-{name}" : profile.AgentId.Trim();
    var device = string.IsNullOrWhiteSpace(profile.Device) ? machine : profile.Device.Trim();

    return new ErpHeartbeatTarget(name, baseUrl, auth, secretHeader, agentId, device);
}

static HttpClient BuildErpClient(int timeoutMs, int connectTimeoutMs)
{
    var handler = new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMs)
    };
    return new HttpClient(handler)
    {
        Timeout = TimeSpan.FromMilliseconds(timeoutMs)
    };
}

static async Task<string?> SendErpHeartbeatAsync(HttpClient client, ErpHeartbeatTarget target, string localBaseUrl)
{
    var payload = BuildErpHeartbeatPayload(target, localBaseUrl);
    var json = JsonSerializer.Serialize(payload);
    using var request = new HttpRequestMessage(
        HttpMethod.Post,
        $"{target.BaseUrl}/api/method/rfidenter.rfidenter.api.register_agent")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    if (!string.IsNullOrWhiteSpace(target.AuthHeader))
    {
        request.Headers.TryAddWithoutValidation("Authorization", target.AuthHeader);
    }
    if (!string.IsNullOrWhiteSpace(target.SecretHeader))
    {
        request.Headers.TryAddWithoutValidation("X-RFIDenter-Token", target.SecretHeader);
    }

    using var response = await client.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        return ParseFrappeError(body, response.StatusCode);
    }
    return null;
}

static Dictionary<string, object?> BuildErpHeartbeatPayload(ErpHeartbeatTarget target, string localBaseUrl)
{
    var (uiHost, uiPort, uiUrls) = BuildUiMeta(localBaseUrl);
    return new Dictionary<string, object?>
    {
        ["agent_id"] = target.AgentId,
        ["agent_uid"] = AgentIdentity.GetStableAgentUid(),
        ["device"] = target.Device,
        ["ui_urls"] = uiUrls,
        ["ui_host"] = uiHost,
        ["ui_port"] = uiPort,
        ["platform"] = ResolvePlatform(),
        ["version"] = "zebra-bridge-v1",
        ["pid"] = Environment.ProcessId,
        ["kind"] = "zebra",
        ["ts"] = NowMs()
    };
}

static (string Host, int Port, List<string> Urls) BuildUiMeta(string localBaseUrl)
{
    var urls = new List<string>();
    var trimmed = (localBaseUrl ?? string.Empty).Trim().TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(trimmed))
    {
        urls.Add(trimmed);
    }

    var host = string.Empty;
    var port = 0;
    if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
    {
        host = uri.Host;
        port = uri.Port;
        if (!string.IsNullOrWhiteSpace(host) && host is not "0.0.0.0" and not "::" && port > 0)
        {
            var loopback = $"http://127.0.0.1:{port}";
            if (!urls.Any(u => string.Equals(u, loopback, StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(loopback);
            }
        }
    }

    return (host, port, urls);
}

static string ResolvePlatform()
{
    if (OperatingSystem.IsWindows())
    {
        return "windows";
    }
    if (OperatingSystem.IsLinux())
    {
        return "linux";
    }
    if (OperatingSystem.IsMacOS())
    {
        return "macos";
    }
    return "unknown";
}

static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

static string TrimForTui(string? value, int maxLen = 80)
{
    var s = (value ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(s))
    {
        return "unknown";
    }
    return s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}

static int GetEnvInt(string key, int fallback, int min, int max)
{
    var raw = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(raw))
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

static string ParseFrappeError(string body, System.Net.HttpStatusCode status)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        return $"HTTP {(int)status}";
    }

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("_server_messages", out var serverMessages))
            {
                return serverMessages.ToString();
            }
            if (root.TryGetProperty("message", out var message))
            {
                return message.ToString();
            }
            if (root.TryGetProperty("error", out var error))
            {
                return error.ToString();
            }
        }
    }
    catch
    {
    }

    return body.Length > 400 ? body[..400] : body;
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
    Console.WriteLine("  zebra-cli tui [--url http://127.0.0.1:18000] [--setup]");
    Console.WriteLine("  zebra-cli setup [--online|--offline] [--erp-url URL] [--erp-token TOKEN] [--device NAME]");
    Console.WriteLine("  zebra-cli config");
    Console.WriteLine("  zebra-cli version");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  zebra-cli encode --epc 3034257BF7194E4000000001 --copies 2");
    Console.WriteLine("  zebra-cli encode-batch --items 3034AA:1,3034BB:2");
    Console.WriteLine("  zebra-cli encode-batch --mode auto --count 5");
    Console.WriteLine("  zebra-cli transceive --zpl \"^XA^HH^XZ\"");
}

static string ToJson(object value)
{
    return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
}

static bool RunSetupWizard(bool allowSkip)
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Setup requires an interactive terminal.");
        return false;
    }

    var existing = TryLoadErpProfile();
    var currentMode = existing is null || !existing.RpcEnabled || !existing.Enabled ? "OFFLINE" : "ONLINE";

    Console.Clear();
    Console.WriteLine("ZebraBridge Setup");
    Console.WriteLine("=================");
    Console.WriteLine($"Current mode: {currentMode}");
    if (existing is not null && !string.IsNullOrWhiteSpace(existing.BaseUrl))
    {
        Console.WriteLine($"Current ERP : {existing.BaseUrl}");
    }
    Console.WriteLine();
    Console.WriteLine("Choose mode:");
    Console.WriteLine("  [1] Online (ERP control)");
    Console.WriteLine("  [2] Offline (local only)");
    if (allowSkip)
    {
        Console.WriteLine("  [Enter] Keep current");
    }
    Console.WriteLine("  [Q] Quit");
    Console.WriteLine();
    Console.Write("Select: ");

    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (allowSkip && key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return true;
        }
        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            Console.WriteLine();
            return false;
        }
        if (key.KeyChar == '1')
        {
            Console.WriteLine("Online");
            break;
        }
        if (key.KeyChar == '2')
        {
            Console.WriteLine("Offline");
            WriteErpConfig(enabled: false, baseUrl: string.Empty, auth: string.Empty, device: string.Empty);
            Console.WriteLine("ERP mode set to OFFLINE.");
            Console.WriteLine("Press Enter to continue...");
            Console.ReadLine();
            return true;
        }
    }

    var erpUrl = PromptWithDefault("ERP URL", existing?.BaseUrl);
    while (string.IsNullOrWhiteSpace(erpUrl))
    {
        Console.WriteLine("ERP URL is required for online mode.");
        erpUrl = Prompt("ERP URL: ");
    }

    var tokenLabel = string.IsNullOrWhiteSpace(existing?.Auth)
        ? "ERP Token (api_key:api_secret or 'token ...')"
        : "ERP Token (leave empty to keep current)";
    var token = PromptSecret($"{tokenLabel}: ");
    if (string.IsNullOrWhiteSpace(token))
    {
        token = existing?.Auth ?? string.Empty;
    }
    while (string.IsNullOrWhiteSpace(token))
    {
        Console.WriteLine("ERP Token is required for online mode.");
        token = PromptSecret("ERP Token: ");
    }

    var deviceDefault = string.IsNullOrWhiteSpace(existing?.Device) ? Environment.MachineName : existing!.Device;
    var device = PromptWithDefault("Local device name", deviceDefault);
    if (string.IsNullOrWhiteSpace(device))
    {
        device = deviceDefault;
    }

    var normalizedUrl = NormalizeBaseUrl(erpUrl);
    WriteErpConfig(enabled: true, baseUrl: normalizedUrl, auth: token, device: device);
    Console.WriteLine($"ERP mode set to ONLINE. Target: {normalizedUrl}");
    Console.WriteLine("Press Enter to continue...");
    Console.ReadLine();
    return true;
}

static string PromptWithDefault(string label, string? fallback)
{
    var value = Prompt(string.IsNullOrWhiteSpace(fallback) ? $"{label}: " : $"{label} [{fallback}]: ");
    return string.IsNullOrWhiteSpace(value) ? (fallback ?? string.Empty) : value;
}

static string Prompt(string message)
{
    Console.Write(message);
    return (Console.ReadLine() ?? string.Empty).Trim();
}

static string PromptSecret(string message)
{
    if (Console.IsInputRedirected)
    {
        return string.Empty;
    }

    Console.Write(message);
    var buffer = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            break;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length == 0)
            {
                continue;
            }
            buffer.Length -= 1;
            Console.Write("\b \b");
            continue;
        }
        if (!char.IsControl(key.KeyChar))
        {
            buffer.Append(key.KeyChar);
            Console.Write("*");
        }
    }
    Console.WriteLine();
    return buffer.ToString();
}

static void WriteErpConfig(bool enabled, string baseUrl, string auth, string device)
{
    var path = StatePaths.GetErpConfigPath();
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var profile = new Dictionary<string, object?>
    {
        ["rpcEnabled"] = enabled,
        ["enabled"] = enabled,
        ["overrideEnv"] = true,
        ["baseUrl"] = NormalizeBaseUrl(baseUrl),
        ["auth"] = NormalizeAuth(auth),
        ["device"] = device?.Trim() ?? string.Empty
    };

    var payload = new Dictionary<string, object?>
    {
        ["erp"] = new Dictionary<string, object?>
        {
            ["activeProfile"] = "local",
            ["profiles"] = new Dictionary<string, object?> { ["local"] = profile }
        }
    };

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
    Console.WriteLine($"ERP config saved to: {path}");
}

static string NormalizeBaseUrl(string? raw)
{
    var value = (raw ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }
    if (!value.Contains("://", StringComparison.Ordinal))
    {
        value = "http://" + value;
    }
    return value.EndsWith("/", StringComparison.Ordinal) ? value[..^1] : value;
}

static string NormalizeAuth(string? raw)
{
    var value = (raw ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }
    return value.StartsWith("token ", StringComparison.OrdinalIgnoreCase) ? value : $"token {value}";
}

static bool HasErpConfig()
{
    var path = StatePaths.GetErpConfigPath();
    return File.Exists(path);
}

static ErpProfile? TryLoadErpProfile()
{
    var path = StatePaths.GetErpConfigPath();
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        var raw = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("erp", out var erp) || erp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var profiles = erp.TryGetProperty("profiles", out var profilesElement) &&
                       profilesElement.ValueKind == JsonValueKind.Object
            ? profilesElement
            : default;

        var activeProfile = ReadString(erp, "activeProfile", ReadString(erp, "active_profile", "local"));
        if (profiles.ValueKind == JsonValueKind.Object && profiles.TryGetProperty(activeProfile, out var profileElement))
        {
            return ParseProfile(activeProfile, profileElement);
        }

        if (profiles.ValueKind == JsonValueKind.Object)
        {
            foreach (var profile in profiles.EnumerateObject())
            {
                var parsed = ParseProfile(profile.Name, profile.Value);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
    }
    catch
    {
        return null;
    }

    return null;
}

static ErpProfile? ParseProfile(string name, JsonElement profile)
{
    if (profile.ValueKind != JsonValueKind.Object)
    {
        return null;
    }

    var rpcEnabled = ReadBool(profile, "rpcEnabled", ReadBool(profile, "rpc_enabled", true));
    var enabled = ReadBool(profile, "enabled", true);
    var baseUrl = ReadString(profile, "baseUrl", ReadString(profile, "base_url", string.Empty));
    var auth = ReadString(profile, "auth", ReadString(profile, "authorization", string.Empty));
    var secret = ReadString(profile, "secret", string.Empty);
    var agentId = ReadString(profile, "agentId", ReadString(profile, "agent_id", string.Empty));
    var device = ReadString(profile, "device", string.Empty);

    return new ErpProfile(name, rpcEnabled, enabled, baseUrl, auth, secret, agentId, device);
}

static string ReadString(JsonElement element, string name, string fallback)
{
    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
    {
        return fallback;
    }

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? fallback,
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => fallback
    };
}

static bool ReadBool(JsonElement element, string name, bool fallback)
{
    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
    {
        return fallback;
    }

    return value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
        JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed != 0,
        _ => fallback
    };
}

sealed record CliContext(
    PrinterOptions PrinterOptions,
    EncodeService EncodeService,
    PrinterControlService PrinterControl);

sealed record ErpHeartbeatTarget(
    string Name,
    string BaseUrl,
    string AuthHeader,
    string SecretHeader,
    string AgentId,
    string Device);

sealed record ErpProfile(
    string Name,
    bool RpcEnabled,
    bool Enabled,
    string BaseUrl,
    string Auth,
    string Secret,
    string AgentId,
    string Device);

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
