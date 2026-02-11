using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZebraBridge.Core;

namespace ZebraBridge.Web;

public sealed class ScaleReaderService : BackgroundService
{
    private static readonly Regex WeightRegex = new(
        @"([-+]?\d+(?:[.,]\d+)?)\s*(kg|g|lb|lbs|oz)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StableRegex = new(@"\bST\b|\bSTABLE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnstableRegex = new(@"\bUS\b|\bUNSTABLE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ScaleOptions _options;
    private readonly IScaleState _scaleState;
    private readonly ErpAgentOptions _erpOptions;
    private readonly ErpAgentRuntimeConfig? _erpTarget;
    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<ScaleReaderService> _logger;
    private readonly SemaphoreSlim _pushLock = new(1, 1);
    private readonly string _portCacheFile;

    private int _portCacheWritten;
    private string? _lastResolvedPort;
    private bool _lastResolvedFromCache;

    private long _lastPushTs;
    private double? _lastPushWeight;
    private bool? _lastPushStable;
    private bool _pushAuthFailed;

    public ScaleReaderService(
        ScaleOptions options,
        IScaleState scaleState,
        ErpAgentOptions erpOptions,
        IHttpClientFactory clientFactory,
        ILogger<ScaleReaderService> logger)
    {
        _options = options;
        _scaleState = scaleState;
        _erpOptions = erpOptions;
        _erpTarget = ResolveErpTarget(erpOptions);
        _clientFactory = clientFactory;
        _logger = logger;
        _portCacheFile = ResolvePortCacheFile();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            UpdateError("Scale disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var portName = ResolvePortName();
            if (string.IsNullOrWhiteSpace(portName))
            {
                UpdateError("Scale port not found.");
                await DelaySafe(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            SerialPort? port = null;
            try
            {
                port = OpenPort(portName);
                UpdateOk(portName);
                await ReadLoopAsync(port, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_lastResolvedFromCache && !string.IsNullOrWhiteSpace(_lastResolvedPort) &&
                    string.Equals(portName, _lastResolvedPort, StringComparison.OrdinalIgnoreCase))
                {
                    // Cached port became invalid (device unplugged/permissions changed). Clear cache so the next
                    // iteration can re-detect automatically.
                    InvalidatePortCache();
                    _lastResolvedFromCache = false;
                }

                _logger.LogWarning(ex, "Scale port error: {Port}", portName);
                UpdateError($"Scale error: {ex.Message}", portName);
                await DelaySafe(TimeSpan.FromSeconds(2), stoppingToken);
            }
            finally
            {
                try
                {
                    port?.Close();
                }
                catch
                {
                }
            }
        }
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken stoppingToken)
    {
        var buffer = new byte[256];
        var rawBuffer = new StringBuilder(256);
        double? lastWeight = null;
        bool? lastStable = null;
        var idleTimeout = GetReconnectIdleTimeout();
        var lastDataAt = DateTimeOffset.UtcNow;

        bool ShouldReconnectOnIdle()
        {
            return idleTimeout.HasValue && DateTimeOffset.UtcNow - lastDataAt > idleTimeout.Value;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await ReadWithTimeoutAsync(port, buffer, stoppingToken);
            }
            catch (TimeoutException)
            {
                if (ShouldReconnectOnIdle())
                {
                    UpdateError("Scale idle timeout; reconnecting.", port.PortName);
                    return;
                }
                continue;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                if (ShouldReconnectOnIdle())
                {
                    UpdateError("Scale idle timeout; reconnecting.", port.PortName);
                    return;
                }
                continue;
            }
            catch (IOException ex)
            {
                UpdateError($"Scale disconnected: {ex.Message}", port.PortName);
                return;
            }
            catch (InvalidOperationException ex)
            {
                UpdateError($"Scale disconnected: {ex.Message}", port.PortName);
                return;
            }

            if (read <= 0)
            {
                if (ShouldReconnectOnIdle())
                {
                    UpdateError("Scale idle timeout; reconnecting.", port.PortName);
                    return;
                }
                continue;
            }

            lastDataAt = DateTimeOffset.UtcNow;
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            AppendBuffer(rawBuffer, text, 200);
            var parsed = TryParseWeight(rawBuffer.ToString(), _options.Unit);
            if (parsed is null)
            {
                continue;
            }

            var (weight, unit, stable) = parsed.Value;
            if (!HasWeightChanged(weight, stable, lastWeight, lastStable, _options.MinChange))
            {
                continue;
            }

            lastWeight = weight;
            lastStable = stable;

            PersistPortCacheOnce(port.PortName);

            _scaleState.Update(new ScaleReading(
                Ok: true,
                Weight: weight,
                Unit: unit,
                Stable: stable,
                TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Port: port.PortName,
                Raw: rawBuffer.ToString(),
                Error: string.Empty
            ));

            await MaybePushAsync(weight, unit, stable, port.PortName, stoppingToken);
        }
    }

    private async Task<int> ReadWithTimeoutAsync(SerialPort port, byte[] buffer, CancellationToken stoppingToken)
    {
        var timeoutMs = (int)Math.Max(50, _options.TimeoutSec * 1000);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (timeoutMs > 0)
        {
            readCts.CancelAfter(timeoutMs);
        }

        try
        {
            return await port.BaseStream.ReadAsync(buffer, readCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private string ResolvePortName()
    {
        _lastResolvedPort = null;
        _lastResolvedFromCache = false;

        var configured = NormalizePortName(_options.Port);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            configured = ResolveConfiguredPortPath(configured);
            _lastResolvedPort = configured;
            return configured;
        }

        var cached = LoadPortCache();
        if (!string.IsNullOrWhiteSpace(cached))
        {
            _lastResolvedPort = cached;
            _lastResolvedFromCache = true;
            return cached;
        }

        var ports = ScalePortEnumerator.ListPorts()
            .Select(port => NormalizePortName(port.Device))
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .Where(IsAllowedAutoPort)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ports.Count == 0)
        {
            return string.Empty;
        }

        if (ports.Count == 1)
        {
            _lastResolvedPort = ports[0];
            return ports[0];
        }

        // On Linux, USB-serial scales almost always appear as ttyUSB*/ttyACM*.
        // Re-order ports so they are probed first. (Still probes all if needed.)
        if (OperatingSystem.IsLinux())
        {
            ports = ports
                .OrderByDescending(IsUsbSerialPort)
                .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var timeoutSec = Math.Clamp(_options.DetectTimeoutSec, 0.05, 2.0);
        var timeout = TimeSpan.FromSeconds(timeoutSec);

        var (detectedPort, foundWeight) = DetectPortByProbing(ports, timeout);
        if (string.IsNullOrWhiteSpace(detectedPort))
        {
            return string.Empty;
        }

        _lastResolvedPort = detectedPort;
        if (foundWeight)
        {
            PersistPortCacheOnce(detectedPort);
        }
        return detectedPort;
    }

    private static string NormalizePortName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string ResolveConfiguredPortPath(string configured)
    {
        var value = NormalizePortName(configured);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsLinux())
        {
            return value;
        }

        try
        {
            if (!File.Exists(value))
            {
                return value;
            }

            var info = new FileInfo(value);
            var target = info.ResolveLinkTarget(true);
            if (target is null)
            {
                return value;
            }

            var resolved = NormalizePortName(target.FullName);
            if (!string.IsNullOrWhiteSpace(resolved) && resolved.StartsWith("/dev/", StringComparison.Ordinal))
            {
                return resolved;
            }

            return value;
        }
        catch
        {
            return value;
        }
    }

    private static bool IsUsbSerialPort(string port)
    {
        var p = port ?? string.Empty;
        return p.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase)
               || p.Contains("ttyACM", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedAutoPort(string port)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            return false;
        }

        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        var p = port.Trim();
        return !p.StartsWith("/dev/pts/", StringComparison.OrdinalIgnoreCase)
               && !p.StartsWith("pts/", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(p, "/dev/ptmx", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(p, "ptmx", StringComparison.OrdinalIgnoreCase);
    }

    private (string Port, bool FoundWeight) DetectPortByProbing(IReadOnlyList<string> ports, TimeSpan timeout)
    {
        var concurrency = Math.Clamp(_options.DetectConcurrency, 1, 32);
        string? foundPort = null;
        string? dataPort = null;

        try
        {
            Parallel.ForEach(
                ports,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                (port, state) =>
                {
                    if (Volatile.Read(ref foundPort) is not null)
                    {
                        state.Stop();
                        return;
                    }

                    var probe = ProbePortForWeight(port, timeout);
                    if (!probe.FoundWeight)
                    {
                        if (probe.HasData)
                        {
                            Interlocked.CompareExchange(ref dataPort, port, null);
                        }
                        return;
                    }

                    if (Interlocked.CompareExchange(ref foundPort, port, null) is null)
                    {
                        state.Stop();
                    }
                });
        }
        catch
        {
            // Fall back to sequential detection if parallel probing fails for any reason.
            foreach (var port in ports)
            {
                var probe = ProbePortForWeight(port, timeout);
                if (probe.FoundWeight)
                {
                    return (port, true);
                }
                if (probe.HasData && dataPort is null)
                {
                    dataPort = port;
                }
            }
        }

        if (foundPort is not null)
        {
            return (foundPort, true);
        }

        // If we couldn't confidently parse a weight, pick the first port that has any data,
        // otherwise fall back to the first port in the list (keeps compatibility with older
        // non-streaming scale setups).
        if (dataPort is not null)
        {
            return (dataPort, false);
        }

        return ports.Count > 0 ? (ports[0], false) : (string.Empty, false);
    }

    private TimeSpan? GetReconnectIdleTimeout()
    {
        var seconds = _options.ReconnectIdleSec;
        if (seconds <= 0)
        {
            return null;
        }

        var clamped = Math.Clamp(seconds, 0.2, 60.0);
        return TimeSpan.FromSeconds(clamped);
    }

    private SerialPort OpenPort(string portName)
    {
        var parity = ParseParity(_options.Parity);
        var stopBits = ParseStopBits(_options.Stopbits);
        var port = new SerialPort(portName, _options.Baudrate, parity, _options.Bytesize, stopBits)
        {
            ReadTimeout = (int)Math.Max(50, _options.TimeoutSec * 1000),
            WriteTimeout = 1000
        };

        port.Open();
        return port;
    }

    private static string ResolvePortCacheFile()
    {
        var overridePath = Environment.GetEnvironmentVariable("ZEBRA_SCALE_CACHE_FILE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath.Trim();
        }

        // XLCU sets XDG_CACHE_HOME to "<zebra_repo>/.cache" so this persists on the host even in Docker.
        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCache))
        {
            return Path.Combine(xdgCache.Trim(), "zebra-scale.by-id");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".cache", "zebra-scale.by-id");
        }

        return Path.Combine(AppContext.BaseDirectory, "zebra-scale.by-id");
    }

    private string? LoadPortCache()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_portCacheFile) || !File.Exists(_portCacheFile))
            {
                return null;
            }

            var value = (File.ReadAllText(_portCacheFile) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (OperatingSystem.IsLinux() &&
                (value.StartsWith("/dev/", StringComparison.Ordinal) ||
                 value.StartsWith("/run/", StringComparison.Ordinal)) &&
                !File.Exists(value))
            {
                return null;
            }

            return value;
        }
        catch
        {
            return null;
        }
    }

    private void InvalidatePortCache()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_portCacheFile) && File.Exists(_portCacheFile))
            {
                File.Delete(_portCacheFile);
            }
        }
        catch
        {
        }

        Interlocked.Exchange(ref _portCacheWritten, 0);
    }

    private void PersistPortCacheOnce(string portName)
    {
        if (Interlocked.CompareExchange(ref _portCacheWritten, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var value = NormalizePortName(portName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_portCacheFile))
            {
                return;
            }

            // If the port is already a stable by-id path, keep it.
            if (OperatingSystem.IsLinux() && value.StartsWith("/dev/serial/by-id/", StringComparison.Ordinal))
            {
                WritePortCache(value);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                var normalized = NormalizeLinuxPortPath(value);
                var byId = FindSerialByIdAlias(normalized);
                WritePortCache(string.IsNullOrWhiteSpace(byId) ? normalized : byId);
                return;
            }

            WritePortCache(value);
        }
        catch
        {
        }
    }

    private void WritePortCache(string value)
    {
        try
        {
            var dir = Path.GetDirectoryName(_portCacheFile);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_portCacheFile, value.Trim());
        }
        catch
        {
        }
    }

    private static string NormalizeLinuxPortPath(string port)
    {
        var p = (port ?? string.Empty).Trim();
        if (p.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return p;
        }

        if (p.StartsWith("tty", StringComparison.OrdinalIgnoreCase))
        {
            return "/dev/" + p;
        }

        return p;
    }

    private static string? FindSerialByIdAlias(string portPath)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            var dir = "/dev/serial/by-id";
            if (!Directory.Exists(dir))
            {
                return null;
            }

            var normalized = NormalizeLinuxPortPath(portPath);
            foreach (var entry in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var info = new FileInfo(entry);
                    var target = info.ResolveLinkTarget(true);
                    if (target is null)
                    {
                        continue;
                    }

                    var targetPath = NormalizeLinuxPortPath(target.FullName);
                    if (string.Equals(targetPath, normalized, StringComparison.Ordinal))
                    {
                        return entry;
                    }
                }
                catch
                {
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (double Weight, string Unit, bool? Stable)? TryParseWeight(string text, string defaultUnit)
    {
        var matches = WeightRegex.Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        var match = matches[^1];
        var raw = match.Groups[1].Value;
        var unit = match.Groups[2].Value;
        unit = string.IsNullOrWhiteSpace(unit) ? defaultUnit : unit;
        unit = unit.Trim().ToLowerInvariant();

        if (!double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
        {
            return null;
        }

        if (weight is < -1_000_000 or > 1_000_000)
        {
            return null;
        }

        bool? stable = null;
        if (UnstableRegex.IsMatch(text))
        {
            stable = false;
        }
        else if (StableRegex.IsMatch(text))
        {
            stable = true;
        }

        return (weight, unit, stable);
    }

    private static bool HasWeightChanged(
        double weight,
        bool? stable,
        double? lastWeight,
        bool? lastStable,
        double minChange)
    {
        if (lastWeight is null)
        {
            return true;
        }

        if (stable != lastStable)
        {
            return true;
        }

        return Math.Abs(weight - lastWeight.Value) >= Math.Max(minChange, 0);
    }

    private (bool FoundWeight, bool HasData) ProbePortForWeight(string portName, TimeSpan timeout)
    {
        try
        {
            using var port = OpenPort(portName);
            var buffer = new StringBuilder(256);
            var stopwatch = Stopwatch.StartNew();
            var hasData = false;

            while (stopwatch.Elapsed < timeout)
            {
                var chunk = port.ReadExisting();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    hasData = true;
                    AppendBuffer(buffer, chunk, 200);
                    if (TryParseWeight(buffer.ToString(), _options.Unit) is not null)
                    {
                        return (true, true);
                    }
                }

                Thread.Sleep(30);
            }

            return (false, hasData);
        }
        catch
        {
            return (false, false);
        }
    }

    private async Task MaybePushAsync(
        double weight,
        string unit,
        bool? stable,
        string port,
        CancellationToken token)
    {
        if (!_options.PushEnabled)
        {
            return;
        }
        if (_pushAuthFailed)
        {
            return;
        }

        var baseUrl = NormalizeBaseUrl(_erpTarget?.BaseUrl ?? _erpOptions.BaseUrl);
        var auth = _erpTarget?.AuthHeader ?? NormalizeAuth(_erpOptions.Auth);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(auth))
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_lastPushTs > 0 && nowMs - _lastPushTs < _options.PushMinIntervalMs)
        {
            return;
        }

        if (_lastPushWeight is not null &&
            Math.Abs(weight - _lastPushWeight.Value) < _options.PushMinDelta &&
            stable == _lastPushStable)
        {
            return;
        }

        if (!await _pushLock.WaitAsync(0, token))
        {
            return;
        }

        try
        {
            var device = string.IsNullOrWhiteSpace(_options.Device)
                ? (_erpTarget?.Device ?? _erpOptions.Device)
                : _options.Device;
            if (string.IsNullOrWhiteSpace(device))
            {
                device = Environment.MachineName;
            }

            var payload = new Dictionary<string, object?>
            {
                ["weight"] = weight,
                ["unit"] = unit,
                ["stable"] = stable,
                ["port"] = port,
                ["ts"] = nowMs,
                ["device"] = device
            };

            var headers = new Dictionary<string, string> { ["Authorization"] = auth };
            var secret = _erpTarget?.Secret ?? _erpOptions.Secret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                secret = auth;
            }
            if (!string.IsNullOrWhiteSpace(secret))
            {
                headers["X-RFIDenter-Token"] = secret;
            }

            var url = $"{baseUrl}{_options.PushEndpoint}";
            await PostJsonAsync(url, payload, headers, token);

            _lastPushTs = nowMs;
            _lastPushWeight = weight;
            _lastPushStable = stable;
        }
        catch (Exception ex)
        {
            if (IsAuthFailure(ex))
            {
                _pushAuthFailed = true;
                _logger.LogInformation("Scale push auth failed. Push disabled until restart.");
            }
            else
            {
                _logger.LogWarning(ex, "Scale push failed.");
            }
        }
        finally
        {
            _pushLock.Release();
        }
    }

    private async Task PostJsonAsync(
        string url,
        Dictionary<string, object?> payload,
        Dictionary<string, string> headers,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.ExpectContinue = false;
        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header.Value))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var client = _clientFactory.CreateClient("zebra");
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            var trimmed = string.IsNullOrWhiteSpace(body) ? string.Empty : body.Trim();
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}). {trimmed}");
        }
    }

    private static bool IsAuthFailure(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("authenticationerror", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || msg.Contains(" 401", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("401", StringComparison.OrdinalIgnoreCase);
    }

    private static ErpAgentRuntimeConfig? ResolveErpTarget(ErpAgentOptions options)
    {
        try
        {
            return ErpAgentConfigLoader.Load(options).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeBaseUrl(string? raw)
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

    private static string NormalizeAuth(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return value.StartsWith("token ", StringComparison.OrdinalIgnoreCase) ? value : $"token {value}";
    }

    private void UpdateError(string message, string? port = null)
    {
        _scaleState.Update(new ScaleReading(
            Ok: false,
            Weight: null,
            Unit: _options.Unit,
            Stable: null,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Port: port ?? string.Empty,
            Raw: string.Empty,
            Error: message
        ));
    }

    private void UpdateOk(string portName)
    {
        _scaleState.Update(new ScaleReading(
            Ok: true,
            Weight: null,
            Unit: _options.Unit,
            Stable: null,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Port: portName,
            Raw: string.Empty,
            Error: string.Empty
        ));
    }

    private static void AppendBuffer(StringBuilder buffer, string chunk, int maxLength)
    {
        buffer.Append(chunk);
        if (buffer.Length <= maxLength)
        {
            return;
        }

        buffer.Remove(0, buffer.Length - maxLength);
    }

    private static Parity ParseParity(string? parity)
    {
        return parity?.Trim().ToLowerInvariant() switch
        {
            "even" => Parity.Even,
            "odd" => Parity.Odd,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => Parity.None
        };
    }

    private static StopBits ParseStopBits(double stopBits)
    {
        return stopBits switch
        {
            1.5 => StopBits.OnePointFive,
            2 => StopBits.Two,
            _ => StopBits.One
        };
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
