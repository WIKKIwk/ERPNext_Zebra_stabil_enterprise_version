using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ZebraBridge.Application;
using ZebraBridge.Core;

namespace ZebraBridge.Web;

public sealed class ScaleAutoPrintService : BackgroundService
{
    private readonly ScaleOptions _options;
    private readonly IScaleState _scaleState;
    private readonly PrinterOptions _printerOptions;
    private readonly ErpAgentRuntimeConfig? _erpTarget;
    private readonly IHttpClientFactory _clientFactory;
    private readonly IPrinterTransportFactory _transportFactory;
    private readonly PrintCoordinator _printCoordinator;
    private readonly ILogger<ScaleAutoPrintService> _logger;

    private bool _awaitingEmpty = true;
    private long _stableSinceMs;
    private double? _lastWeight;
    private double? _lastPrintedWeight;

    public ScaleAutoPrintService(
        ScaleOptions options,
        IScaleState scaleState,
        PrinterOptions printerOptions,
        ErpAgentOptions erpOptions,
        IHttpClientFactory clientFactory,
        IPrinterTransportFactory transportFactory,
        PrintCoordinator printCoordinator,
        ILogger<ScaleAutoPrintService> logger)
    {
        _options = options;
        _scaleState = scaleState;
        _printerOptions = printerOptions;
        _erpTarget = ResolveErpTarget(erpOptions);
        _clientFactory = clientFactory;
        _transportFactory = transportFactory;
        _printCoordinator = printCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoPrintEnabled)
        {
            _logger.LogInformation("Auto-print disabled.");
            return;
        }

        if (_erpTarget is null || string.IsNullOrWhiteSpace(_erpTarget.BaseUrl))
        {
            _logger.LogWarning("Auto-print disabled: ERP target is not configured.");
            return;
        }

        var pollMs = Math.Clamp(_options.AutoPrintPollMs, 100, 2000);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-print tick failed.");
            }

            await Task.Delay(pollMs, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        if (_erpTarget is null)
        {
            return;
        }

        var reading = _scaleState.Latest;
        if (!reading.Ok || reading.Weight is null)
        {
            return;
        }

        var weight = reading.Weight.Value;
        var stableMarker = reading.Stable;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (weight <= _options.AutoPrintEmptyThreshold)
        {
            ResetAwaitingEmpty(weight);
            return;
        }

        if (_awaitingEmpty)
        {
            if (ShouldResetAfterDrop(weight))
            {
                ResetAwaitingEmpty(weight);
            }
            return;
        }

        if (stableMarker == false)
        {
            _stableSinceMs = 0;
            _lastWeight = weight;
            return;
        }

        if (_lastWeight is null || Math.Abs(weight - _lastWeight.Value) >= Math.Max(_options.MinChange, 0.0))
        {
            _stableSinceMs = nowMs;
            _lastWeight = weight;
        }

        if (_stableSinceMs == 0)
        {
            _stableSinceMs = nowMs;
        }

        if (nowMs - _stableSinceMs < _options.AutoPrintStableMs)
        {
            return;
        }

        if (weight < _options.AutoPrintPlacementMinWeight)
        {
            return;
        }

        var deviceId = string.IsNullOrWhiteSpace(_erpTarget.Device) ? _erpTarget.AgentId : _erpTarget.Device;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _logger.LogWarning("Auto-print skipped: device_id is empty.");
            return;
        }

        var snapshot = await GetDeviceSnapshotAsync(deviceId, token);
        if (snapshot is null || snapshot.Status != "Running")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.CurrentProduct) || string.IsNullOrWhiteSpace(snapshot.CurrentBatch))
        {
            return;
        }

        var requestId = $"auto-{deviceId}-{nowMs}";
        var tag = await CreateItemTagAsync(snapshot.CurrentProduct, weight, requestId, token);
        if (tag is null)
        {
            return;
        }

        var zpl = BuildItemZpl(tag.Epc, tag.ItemCode, tag.ItemName, tag.Qty, tag.Uom, deviceId, _printerOptions);
        await SendZplAsync(zpl, token);
        var marked = await MarkPrintedAsync(tag.Epc, token);
        if (marked)
        {
            await ReportPrintEventAsync(snapshot, tag, deviceId, token);
        }

        _lastPrintedWeight = weight;
        _awaitingEmpty = true;
        _stableSinceMs = 0;
        _lastWeight = weight;
    }

    private void ResetAwaitingEmpty(double weight)
    {
        _awaitingEmpty = false;
        _stableSinceMs = 0;
        _lastWeight = weight;
    }

    private bool ShouldResetAfterDrop(double weight)
    {
        if (_lastPrintedWeight is null)
        {
            return false;
        }

        var delta = Math.Max(0, _options.AutoPrintResetDelta);
        if (delta <= 0)
        {
            return false;
        }

        return _lastPrintedWeight.Value - weight >= delta;
    }

    private async Task<DeviceSnapshot?> GetDeviceSnapshotAsync(string deviceId, CancellationToken token)
    {
        var payload = new Dictionary<string, object> { ["device_id"] = deviceId };
        var doc = await PostJsonAsync("rfidenter.get_device_snapshot", payload, token);
        if (doc is null)
        {
            return null;
        }

        if (!doc.RootElement.TryGetProperty("message", out var msg))
        {
            return null;
        }

        if (!msg.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        if (!msg.TryGetProperty("state", out var stateEl) || stateEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new DeviceSnapshot(
            Status: GetString(stateEl, "status"),
            CurrentBatch: GetString(stateEl, "current_batch_id"),
            CurrentProduct: GetString(stateEl, "current_product"),
            LastEventSeq: GetLong(stateEl, "last_event_seq", 0));
    }

    private async Task ReportPrintEventAsync(DeviceSnapshot snapshot, ItemTag tag, string deviceId, CancellationToken token)
    {
        if (_erpTarget is null)
        {
            return;
        }

        var batchId = snapshot.CurrentBatch;
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        var nextSeq = Math.Max(1, snapshot.LastEventSeq + 1);
        var payload = new Dictionary<string, object>
        {
            ["event_id"] = Guid.NewGuid().ToString(),
            ["device_id"] = deviceId,
            ["batch_id"] = batchId,
            ["seq"] = nextSeq,
            ["event_type"] = "print_completed",
            ["payload"] = new Dictionary<string, object>
            {
                ["product_id"] = tag.ItemCode,
                ["qty"] = tag.Qty,
                ["uom"] = tag.Uom,
                ["epc"] = tag.Epc
            }
        };

        await PostJsonAsync("rfidenter.edge_event_report", payload, token);
    }

    private async Task<ItemTag?> CreateItemTagAsync(string itemCode, double weight, string requestId, CancellationToken token)
    {
        var payload = new Dictionary<string, object>
        {
            ["item_code"] = itemCode,
            ["qty"] = Math.Round(weight, 3),
            ["uom"] = "",
            ["consume_ant_id"] = 0,
            ["client_request_id"] = requestId
        };

        var doc = await PostJsonAsync("rfidenter.rfidenter.api.zebra_create_item_tag", payload, token);
        if (doc is null)
        {
            return null;
        }

        if (!doc.RootElement.TryGetProperty("message", out var msg))
        {
            return null;
        }

        if (!msg.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var epc = GetString(msg, "epc");
        if (string.IsNullOrWhiteSpace(epc))
        {
            return null;
        }

        var tagEl = msg.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        var itemName = tagEl.ValueKind == JsonValueKind.Object ? GetString(tagEl, "item_name") : "";
        var qty = tagEl.ValueKind == JsonValueKind.Object ? GetDouble(tagEl, "qty", weight) : weight;
        var uom = tagEl.ValueKind == JsonValueKind.Object ? GetString(tagEl, "uom") : _options.Unit;

        return new ItemTag(epc, itemCode, itemName, qty, uom);
    }

    private async Task<bool> MarkPrintedAsync(string epc, CancellationToken token)
    {
        var payload = new Dictionary<string, object> { ["epc"] = epc };
        var doc = await PostJsonAsync("rfidenter.rfidenter.api.zebra_mark_tag_printed", payload, token);
        return doc is not null;
    }

    private async Task SendZplAsync(string zpl, CancellationToken token)
    {
        await _printCoordinator.RunLockedAsync(async () =>
        {
            var transport = _transportFactory.Create();
            var payload = Encoding.ASCII.GetBytes(EnsureEndsWithEol(zpl, "\n"));
            await transport.SendAsync(payload, token);
            return 0;
        }, token);
    }

    private async Task<JsonDocument?> PostJsonAsync(string method, Dictionary<string, object> payload, CancellationToken token)
    {
        if (_erpTarget is null)
        {
            return null;
        }

        var client = _clientFactory.CreateClient("zebra");
        var url = $"{_erpTarget.BaseUrl.TrimEnd('/')}/api/method/{method}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };

        foreach (var header in BuildHeaders(_erpTarget))
        {
            req.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var res = await client.SendAsync(req, token);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("ERP call failed ({Status}): {Method}", res.StatusCode, method);
            return null;
        }

        var stream = await res.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private static string BuildItemZpl(
        string epc,
        string itemCode,
        string itemName,
        double qty,
        string uom,
        string deviceId,
        PrinterOptions printerOptions)
    {
        var epcHex = Epc.Normalize(epc);
        Epc.Validate(epcHex);

        var itemLabel = SanitizeZplText(string.IsNullOrWhiteSpace(itemName) ? itemCode : itemName);
        var qtyLabel = qty > 0
            ? $"{qty.ToString("0.###", CultureInfo.InvariantCulture)} {SanitizeZplText(uom)}".Trim()
            : string.Empty;
        var epcLabel = SanitizeZplText(epcHex);

        var options = new RfidWriteOptions(
            PrintHumanReadable: false,
            Copies: 1,
            FeedAfterEncode: printerOptions.FeedAfterEncode,
            LabelsToTryOnError: printerOptions.LabelsToTryOnError,
            ErrorHandlingAction: printerOptions.ErrorHandlingAction);

        var lines = new List<string>
        {
            "^XA",
            "^LH0,0",
            "^LT0",
            "^FWN",
            ZplBuilder.BuildRfidSetup(options)
        };

        if (options.MemoryBank == 1 && options.WordPointer == 2 && options.AutoAdjustPcBits)
        {
            lines.Add($"^RFW,H,,,A^FD{epcHex}^FS");
        }
        else
        {
            var byteCount = Epc.HexToWordCount(epcHex) * 2;
            lines.Add($"^RFW,H,{options.WordPointer},{byteCount},{options.MemoryBank}^FD{epcHex}^FS");
        }

        if (!string.IsNullOrWhiteSpace(itemLabel))
        {
            lines.Add($"^FO10,20^A0N,36,36^FD{itemLabel}^FS");
        }
        if (!string.IsNullOrWhiteSpace(qtyLabel))
        {
            lines.Add($"^FO10,70^A0N,32,32^FD{qtyLabel}^FS");
        }
        if (!string.IsNullOrWhiteSpace(epcLabel))
        {
            lines.Add($"^FO10,120^A0N,28,28^FD{epcLabel}^FS");
        }

        lines.Add("^PQ1");
        lines.Add("^XZ");
        return string.Join("\n", lines);
    }

    private static string SanitizeZplText(string value)
    {
        var s = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }
        return s.Replace("^", " ").Replace("~", " ").Replace("\r", " ").Replace("\n", " ");
    }

    private static string EnsureEndsWithEol(string input, string eol)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        if (input.EndsWith(eol, StringComparison.Ordinal))
        {
            return input;
        }
        return input + eol;
    }

    private static string GetString(JsonElement obj, string key)
    {
        return obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
    }

    private static double GetDouble(JsonElement obj, string key, double fallback)
    {
        if (!obj.TryGetProperty(key, out var el))
        {
            return fallback;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var val))
        {
            return val;
        }

        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static long GetLong(JsonElement obj, string key, long fallback)
    {
        if (!obj.TryGetProperty(key, out var el))
        {
            return fallback;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var val))
        {
            return val;
        }

        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static ErpAgentRuntimeConfig? ResolveErpTarget(ErpAgentOptions options)
    {
        var configs = ErpAgentConfigLoader.Load(options);
        return configs.Count > 0 ? configs[0] : null;
    }

    private static Dictionary<string, string> BuildHeaders(ErpAgentRuntimeConfig config)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(config.AuthHeader))
        {
            headers["Authorization"] = config.AuthHeader;
        }
        var secret = string.IsNullOrWhiteSpace(config.Secret) ? config.AuthHeader : config.Secret;
        if (!string.IsNullOrWhiteSpace(secret))
        {
            headers["X-RFIDenter-Token"] = secret;
        }
        return headers;
    }

    private sealed record DeviceSnapshot(string Status, string CurrentBatch, string CurrentProduct, long LastEventSeq);

    private sealed record ItemTag(string Epc, string ItemCode, string ItemName, double Qty, string Uom);
}
