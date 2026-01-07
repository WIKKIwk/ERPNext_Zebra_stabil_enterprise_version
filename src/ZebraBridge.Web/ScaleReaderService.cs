using System.Globalization;
using System.IO.Ports;
using System.Text;
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
    private readonly ILogger<ScaleReaderService> _logger;

    public ScaleReaderService(ScaleOptions options, IScaleState scaleState, ILogger<ScaleReaderService> logger)
    {
        _options = options;
        _scaleState = scaleState;
        _logger = logger;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await port.BaseStream.ReadAsync(buffer, stoppingToken);
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (read <= 0)
            {
                continue;
            }

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
        }
    }

    private string ResolvePortName()
    {
        if (!string.IsNullOrWhiteSpace(_options.Port))
        {
            return _options.Port.Trim();
        }

        var ports = ScalePortEnumerator.ListPorts();
        return ports.FirstOrDefault()?.Device ?? string.Empty;
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
