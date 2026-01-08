using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZebraBridge.Core;

namespace ZebraBridge.Infrastructure;

public sealed class FileEpcGenerator : IEpcGenerator
{
    private const int PrefixBytes = 4;
    private const ulong CounterMax = ulong.MaxValue;

    private readonly EpcGeneratorOptions _options;

    public FileEpcGenerator(EpcGeneratorOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<string> NextEpcs(int count)
    {
        if (count < 1 || count > 1000)
        {
            throw new EpcGeneratorException("Count must be between 1 and 1000.");
        }

        var path = StatePaths.GetEpcGeneratorStatePath(_options);
        EnsureDirectory(path);

        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        LockFile(stream);
        try
        {
            var state = ReadOrInitializeState(stream);
            var epcs = new List<string>(count);
            var counter = state.Counter;

            for (var index = 0; index < count; index++)
            {
                if (counter >= CounterMax)
                {
                    throw new EpcGeneratorException("EPC counter overflow; cannot generate additional unique EPC values.");
                }

                counter += 1;
                epcs.Add(state.PrefixHex + counter.ToString("X16"));
            }

            WriteState(stream, new EpcGeneratorState(state.PrefixHex, counter));
            return epcs;
        }
        finally
        {
            UnlockFile(stream);
        }
    }

    private EpcGeneratorState ReadOrInitializeState(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var raw = reader.ReadToEnd().Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            var state = InitializeState();
            WriteState(stream, state);
            return state;
        }

        EpcGeneratorStatePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<EpcGeneratorStatePayload>(
                raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new EpcGeneratorException("Invalid EPC generator state file.");
        }
        catch (JsonException ex)
        {
            throw new EpcGeneratorException("Invalid EPC generator state file.", ex);
        }

        var prefixHex = NormalizePrefix(payload.PrefixHex ?? string.Empty);
        var counter = payload.Counter;
        if (counter > CounterMax)
        {
            throw new EpcGeneratorException("Invalid EPC generator counter value.");
        }

        return new EpcGeneratorState(prefixHex, counter);
    }

    private EpcGeneratorState InitializeState()
    {
        var prefixHex = _options.PrefixHex;
        if (string.IsNullOrWhiteSpace(prefixHex))
        {
            prefixHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(PrefixBytes));
        }

        prefixHex = NormalizePrefix(prefixHex);
        return new EpcGeneratorState(prefixHex, 0);
    }

    private static void WriteState(FileStream stream, EpcGeneratorState state)
    {
        var payload = new EpcGeneratorStatePayload
        {
            SchemaVersion = 1,
            PrefixHex = state.PrefixHex,
            Counter = state.Counter
        };

        var encoded = JsonSerializer.Serialize(payload);

        stream.Seek(0, SeekOrigin.Begin);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(encoded);
        writer.Write('\n');
        writer.Flush();
        try
        {
            stream.Flush(true);
        }
        catch (Exception)
        {
        }
    }

    private static string NormalizePrefix(string prefixHex)
    {
        var upper = prefixHex.Trim().ToUpperInvariant();
        if (upper.Length != PrefixBytes * 2 || upper.Any(c => !IsHexChar(c)))
        {
            throw new EpcGeneratorException("ZEBRA_EPC_PREFIX_HEX must be 8 hex characters (32-bit prefix).");
        }

        return upper;
    }

    private static bool IsHexChar(char value)
    {
        return value is >= '0' and <= '9' or >= 'A' and <= 'F';
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void LockFile(FileStream stream)
    {
        try
        {
            stream.Lock(0, long.MaxValue);
        }
        catch (Exception)
        {
        }
    }

    private static void UnlockFile(FileStream stream)
    {
        try
        {
            stream.Unlock(0, long.MaxValue);
        }
        catch (Exception)
        {
        }
    }

    private sealed record EpcGeneratorState(string PrefixHex, ulong Counter);

    private sealed class EpcGeneratorStatePayload
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("prefix_hex")]
        public string? PrefixHex { get; init; }

        [JsonPropertyName("counter")]
        public ulong Counter { get; init; }
    }
}
