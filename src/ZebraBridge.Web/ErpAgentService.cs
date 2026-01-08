using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ZebraBridge.Application;
using ZebraBridge.Core;
using ZebraBridge.Infrastructure;

namespace ZebraBridge.Web;

public sealed class ErpAgentService : BackgroundService
{
    private readonly ErpAgentOptions _options;
    private readonly PrinterOptions _printerOptions;
    private readonly IEncodeService _encodeService;
    private readonly IPrinterTransportFactory _transportFactory;
    private readonly PrintCoordinator _printCoordinator;
    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<ErpAgentService> _logger;

    public ErpAgentService(
        ErpAgentOptions options,
        PrinterOptions printerOptions,
        IEncodeService encodeService,
        IPrinterTransportFactory transportFactory,
        PrintCoordinator printCoordinator,
        IHttpClientFactory clientFactory,
        ILogger<ErpAgentService> logger)
    {
        _options = options;
        _printerOptions = printerOptions;
        _encodeService = encodeService;
        _transportFactory = transportFactory;
        _printCoordinator = printCoordinator;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("ERP agent disabled (ZEBRA_ERP_AGENT_ENABLED=0).");
            return;
        }

        var configs = ErpAgentConfigLoader.Load(_options);
        if (configs.Count == 0)
        {
            _logger.LogWarning("ERP agent disabled (no valid targets).");
            return;
        }

        var tasks = configs.Select(config => RunAgentAsync(config, stoppingToken)).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task RunAgentAsync(ErpAgentRuntimeConfig config, CancellationToken stoppingToken)
    {
        var heartbeatInterval = TimeSpan.FromMilliseconds(config.HeartbeatIntervalMs);
        var pollInterval = TimeSpan.FromMilliseconds(config.PollIntervalMs);
        var client = _clientFactory.CreateClient();

        var nextHeartbeatAt = DateTimeOffset.UtcNow;
        var nextPollAt = DateTimeOffset.UtcNow;
        var pollFailCount = 0;
        var lastWarnAt = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            if (now >= nextHeartbeatAt)
            {
                try
                {
                    await RegisterAsync(client, config, stoppingToken);
                }
                catch (Exception ex)
                {
                    if (now - lastWarnAt > TimeSpan.FromSeconds(5))
                    {
                        lastWarnAt = now;
                        _logger.LogWarning("ERP register_agent failed ({Name}): {Message}", config.Name, ex.Message);
                    }
                }
                nextHeartbeatAt = now + heartbeatInterval;
            }

            if (now >= nextPollAt)
            {
                try
                {
                    var commands = await PollAsync(client, config, stoppingToken);
                    await ProcessCommandsAsync(client, config, commands, stoppingToken);
                    pollFailCount = 0;
                }
                catch (Exception ex)
                {
                    pollFailCount = Math.Min(pollFailCount + 1, 10);
                    if (now - lastWarnAt > TimeSpan.FromSeconds(5))
                    {
                        lastWarnAt = now;
                        _logger.LogWarning("ERP poll/reply failed ({Name}): {Message}", config.Name, ex.Message);
                    }
                }
                var backoff = pollFailCount == 0
                    ? pollInterval
                    : TimeSpan.FromMilliseconds(Math.Min(30000, pollInterval.TotalMilliseconds * Math.Pow(2, pollFailCount)));
                nextPollAt = now + backoff;
            }

            var nextTick = nextHeartbeatAt < nextPollAt ? nextHeartbeatAt : nextPollAt;
            var delay = nextTick - now;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RegisterAsync(
        HttpClient client,
        ErpAgentRuntimeConfig config,
        CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["agent_id"] = config.AgentId,
            ["device"] = config.Device,
            ["ui_urls"] = BuildUiUrls(),
            ["ui_host"] = Environment.GetEnvironmentVariable("ZEBRA_WEB_HOST") ?? string.Empty,
            ["ui_port"] = ParseEnvInt("ZEBRA_WEB_PORT"),
            ["platform"] = ResolvePlatform(),
            ["version"] = config.Version,
            ["pid"] = Environment.ProcessId,
            ["kind"] = "zebra",
            ["capabilities"] = new Dictionary<string, bool>
            {
                ["encode"] = true,
                ["encode_batch"] = true,
                ["print_zpl"] = true,
                ["transceive"] = true
            },
            ["ts"] = NowMs()
        };

        await PostFrappeAsync(
            client,
            $"{config.BaseUrl}{config.RegisterEndpoint}",
            payload,
            BuildHeaders(config),
            token);
    }

    private async Task<IReadOnlyList<ErpCommand>> PollAsync(
        HttpClient client,
        ErpAgentRuntimeConfig config,
        CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["agent_id"] = config.AgentId,
            ["max"] = config.PollMax,
            ["ts"] = NowMs()
        };

        var message = await PostFrappeAsync(
            client,
            $"{config.BaseUrl}{config.PollEndpoint}",
            payload,
            BuildHeaders(config),
            token);

        return ParseCommands(message);
    }

    private async Task ProcessCommandsAsync(
        HttpClient client,
        ErpAgentRuntimeConfig config,
        IReadOnlyList<ErpCommand> commands,
        CancellationToken token)
    {
        if (commands.Count == 0)
        {
            return;
        }

        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.RequestId) || string.IsNullOrWhiteSpace(command.Command))
            {
                continue;
            }

            if (IsExpired(command))
            {
                await ReplyAsync(client, config, command.RequestId, false, null, "Expired", token);
                continue;
            }

            try
            {
                var result = await ExecuteCommandAsync(command, token);
                await ReplyAsync(client, config, command.RequestId, true, result, string.Empty, token);
            }
            catch (Exception ex)
            {
                await ReplyAsync(client, config, command.RequestId, false, null, ex.Message, token);
            }
        }
    }

    private async Task<object?> ExecuteCommandAsync(ErpCommand command, CancellationToken token)
    {
        var cmd = command.Command.Trim().ToUpperInvariant();
        var args = command.Args;

        if (cmd is "ZEBRA_HEALTH" or "HEALTH" or "PING")
        {
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["service"] = "zebra-bridge-v1"
            };
        }

        if (cmd is "ZEBRA_CONFIG" or "ZEBRA_RUNTIME_CONFIG" or "CONFIG")
        {
            return new Dictionary<string, object?>
            {
                ["zebra_vendor_id"] = $"0x{_printerOptions.VendorId:X4}",
                ["zebra_product_id"] = $"0x{_printerOptions.ProductId:X4}",
                ["zebra_device_path"] = _printerOptions.DevicePath ?? string.Empty,
                ["zebra_usb_timeout_ms"] = _printerOptions.UsbTimeoutMs,
                ["zebra_feed_after_encode"] = _printerOptions.FeedAfterEncode,
                ["zebra_rfid_labels_to_try_on_error"] = _printerOptions.LabelsToTryOnError,
                ["zebra_rfid_error_handling_action"] = _printerOptions.ErrorHandlingAction,
                ["zebra_template_enabled"] = !string.IsNullOrWhiteSpace(_printerOptions.RfidZplTemplate),
                ["zebra_transport"] = _printerOptions.Transport,
                ["zebra_transceive_supported"] = string.Equals(_printerOptions.Transport, "usb", StringComparison.OrdinalIgnoreCase)
            };
        }

        if (cmd is "ZEBRA_USB_DEVICES" or "USB_DEVICES")
        {
            return UsbDeviceSnapshot.BuildPayload(_printerOptions);
        }

        if (cmd is "ZEBRA_PRINT_ZPL" or "PRINT_ZPL")
        {
            var zpl = GetString(args, "zpl");
            var copies = GetInt(args, "copies", 1, 1, 100);
            if (string.IsNullOrWhiteSpace(zpl))
            {
                throw new ArgumentException("zpl is required.");
            }

            var job = EnsureEndsWithEol(zpl, _printerOptions.ZplEol);
            await _printCoordinator.RunLockedAsync(async () =>
            {
                var transport = _transportFactory.Create();
                var payload = Encoding.ASCII.GetBytes(job);
                for (var i = 0; i < copies; i++)
                {
                    await transport.SendAsync(payload, token);
                }
                return 0;
            }, token);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["message"] = "Print job sent."
            };
        }

        if (cmd is "ZEBRA_TRANSCEIVE_ZPL" or "TRANSCEIVE_ZPL")
        {
            var zpl = GetString(args, "zpl");
            var readTimeoutMs = GetInt(args, "read_timeout_ms", 2000, 50, 20000);
            var maxBytes = GetInt(args, "max_bytes", 32768, 1, 262144);
            if (string.IsNullOrWhiteSpace(zpl))
            {
                throw new ArgumentException("zpl is required.");
            }

            var result = await _encodeService.TransceiveAsync(
                new TransceiveRequest(zpl, readTimeoutMs, maxBytes),
                token);
            return new Dictionary<string, object?>
            {
                ["ok"] = result.Ok,
                ["message"] = result.Message,
                ["output"] = result.Output,
                ["output_bytes"] = result.OutputBytes
            };
        }

        if (cmd is "ZEBRA_ENCODE" or "ENCODE")
        {
            var epc = GetString(args, "epc");
            if (string.IsNullOrWhiteSpace(epc))
            {
                throw new ArgumentException("epc is required.");
            }
            var copies = GetInt(args, "copies", 1, 1, 1000);
            var printHuman = GetBool(args, "print_human_readable", false);
            var dryRun = GetBool(args, "dry_run", false);
            var feedAfter = GetBool(args, "feed_after_encode", _printerOptions.FeedAfterEncode);

            var result = await _encodeService.EncodeAsync(
                new EncodeRequest(epc, copies, printHuman, feedAfter, dryRun),
                token);

            return new Dictionary<string, object?>
            {
                ["ok"] = result.Ok,
                ["message"] = result.Message,
                ["epc_hex"] = result.EpcHex,
                ["zpl"] = result.Zpl
            };
        }

        if (cmd is "ZEBRA_ENCODE_BATCH" or "ENCODE_BATCH")
        {
            var mode = GetString(args, "mode", "manual").Trim().ToLowerInvariant();
            var printHuman = GetBool(args, "print_human_readable", false);
            var feedAfter = GetBool(args, "feed_after_encode", _printerOptions.FeedAfterEncode);
            var autoCount = GetInt(args, "auto_count", 1, 1, 1000);

            var items = new List<EncodeBatchItem>();
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("items", out var itemsElement) &&
                itemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var itemEpc = GetString(item, "epc");
                    if (string.IsNullOrWhiteSpace(itemEpc))
                    {
                        continue;
                    }
                    var itemCopies = GetInt(item, "copies", 1, 1, 1000);
                    items.Add(new EncodeBatchItem(itemEpc, itemCopies));
                }
            }

            var batchMode = mode == "auto" ? EncodeBatchMode.Auto : EncodeBatchMode.Manual;
            var batchRequest = new EncodeBatchRequest(batchMode, items, autoCount, printHuman, feedAfter);
            var result = await _encodeService.EncodeBatchAsync(batchRequest, token);

            return new Dictionary<string, object?>
            {
                ["ok"] = result.Ok,
                ["total_labels_requested"] = result.TotalLabelsRequested,
                ["total_labels_succeeded"] = result.TotalLabelsSucceeded,
                ["unique_epcs_succeeded"] = result.UniqueEpcsSucceeded,
                ["items"] = result.Items.Select(item => new Dictionary<string, object?>
                {
                    ["epc_hex"] = item.EpcHex,
                    ["copies"] = item.Copies,
                    ["ok"] = item.Ok,
                    ["message"] = item.Message
                }).ToList()
            };
        }

        throw new ArgumentException($"Unknown command: {command.Command}");
    }

    private async Task ReplyAsync(
        HttpClient client,
        ErpAgentRuntimeConfig config,
        string requestId,
        bool ok,
        object? result,
        string error,
        CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["agent_id"] = config.AgentId,
            ["request_id"] = requestId,
            ["ok"] = ok,
            ["result"] = ok ? result : null,
            ["error"] = ok ? string.Empty : (error ?? "Unknown error"),
            ["ts"] = NowMs()
        };

        await PostFrappeAsync(
            client,
            $"{config.BaseUrl}{config.ReplyEndpoint}",
            payload,
            BuildHeaders(config),
            token);
    }

    private static async Task<JsonElement> PostFrappeAsync(
        HttpClient client,
        string url,
        Dictionary<string, object?> payload,
        Dictionary<string, string> headers,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header.Value))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var response = await client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseFrappeError(body, response.StatusCode));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var message))
        {
            return message.Clone();
        }

        return root.Clone();
    }

    private static IReadOnlyList<ErpCommand> ParseCommands(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("commands", out var commandsElement) ||
            commandsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ErpCommand>();
        }

        var list = new List<ErpCommand>();
        foreach (var item in commandsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var requestId = GetString(item, "request_id", GetString(item, "requestId"));
            var cmd = GetString(item, "cmd");
            var args = item.TryGetProperty("args", out var argsElement) ? argsElement.Clone() : default;
            var ts = GetLong(item, "ts", 0);
            var timeout = GetInt(item, "timeout_sec", GetInt(item, "timeoutSec", 30), 2, 120);

            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(cmd))
            {
                continue;
            }

            list.Add(new ErpCommand(requestId, cmd, args, ts, timeout));
        }

        return list;
    }

    private static Dictionary<string, string> BuildHeaders(ErpAgentRuntimeConfig config)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(config.AuthHeader))
        {
            headers["Authorization"] = config.AuthHeader;
        }
        if (!string.IsNullOrWhiteSpace(config.Secret))
        {
            headers["X-RFIDenter-Token"] = config.Secret;
        }

        return headers;
    }

    private static string ResolvePlatform()
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

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static int ParseEnvInt(string key)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }
        return int.TryParse(raw.Trim(), out var parsed) ? parsed : 0;
    }

    private static List<string> BuildUiUrls()
    {
        var host = (Environment.GetEnvironmentVariable("ZEBRA_WEB_HOST") ?? string.Empty).Trim();
        var port = (Environment.GetEnvironmentVariable("ZEBRA_WEB_PORT") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(port))
        {
            return new List<string>();
        }

        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(host) && host is not "0.0.0.0" and not "::")
        {
            urls.Add($"http://{host}:{port}");
        }
        if (!urls.Contains("http://127.0.0.1:" + port, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add($"http://127.0.0.1:{port}");
        }

        return urls.Take(5).ToList();
    }

    private static bool IsExpired(ErpCommand command)
    {
        if (command.IssuedAtMs <= 0)
        {
            return false;
        }
        var ageMs = NowMs() - command.IssuedAtMs;
        return ageMs > (command.TimeoutSec + 5) * 1000L;
    }

    private static string EnsureEndsWithEol(string text, string eol)
    {
        if (string.IsNullOrWhiteSpace(eol))
        {
            return text;
        }
        return text.EndsWith(eol, StringComparison.Ordinal) ? text : text + eol;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }
        return value > max ? max : value;
    }


    private static int GetInt(JsonElement element, string name, int fallback, int min, int max)
    {
        var value = GetInt(element, name, fallback);
        return Clamp(value, min, max);
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static long GetLong(JsonElement element, string name, long fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static bool GetBool(JsonElement element, string name, bool fallback)
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

    private static string GetString(JsonElement element, string name, string fallback = "")
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

    private static string ParseFrappeError(string body, System.Net.HttpStatusCode status)
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

    private sealed record ErpCommand(
        string RequestId,
        string Command,
        JsonElement Args,
        long IssuedAtMs,
        int TimeoutSec);
}
