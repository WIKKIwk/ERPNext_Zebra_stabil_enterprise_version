using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ZebraBridge.Edge.Adapters;
using ZebraBridge.Edge.Fsm;
using ZebraBridge.Edge.Orchestrator;
using ZebraBridge.Edge.Outbox;
using ZebraBridge.Edge.Runtime;
using ZebraBridge.Edge.Stability;

namespace ZebraBridge.Edge;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        if (string.Equals(args[0], "sim", StringComparison.OrdinalIgnoreCase))
        {
            await RunSimAsync(args.Skip(1).ToArray());
            return;
        }

        var dbPath = args[0];
        var cts = new CancellationTokenSource();

        var settings = new StabilitySettings(
            sigma: 0.01,
            res: 0.01,
            windowSeconds: 1.0,
            eps: 0.03,
            epsAlign: 0.06,
            emptyThreshold: 0.05,
            placementMinWeight: 1.0,
            slopeLimit: 0.02);

        var detector = new StabilityDetector(settings);
        var fsm = new BatchWeighFsm(FsmConfig.Default, detector, settings);

        var actionChannel = Channel.CreateUnbounded<FsmAction>();
        var fsmSignal = new SemaphoreSlim(0);
        var alarmSink = new NullAlarmSink();
        var fsmLoop = new FsmEventLoop(fsm, actionChannel.Writer, 4096, fsmSignal, alarmSink);

        var batchStore = new BatchStateStore(dbPath);
        var printOutbox = new PrintOutboxStore(dbPath);
        var erpOutbox = new ErpOutboxStore(dbPath);

        var printer = new NullPrinterTransport();
        Action<FsmEvent> controlEnqueue = ev => { fsmLoop.EnqueueControl(ev); };
        var orchestrator = new EventOrchestrator(
            batchStore,
            printOutbox,
            erpOutbox,
            maxErpQueue: 10000,
            supportsStatusProbe: printer.SupportsStatusProbe,
            controlEnqueue: controlEnqueue);

        orchestrator.Initialize();

        var printSignal = new SemaphoreSlim(0);
        var erpSignal = new SemaphoreSlim(0);

        var printWorker = new PrintWorker(printOutbox, printer, controlEnqueue, printSignal);
        var erpWorker = new ErpWorker(erpOutbox, new NullErpClient(), erpSignal);

        var scaleAdapter = new NullScaleAdapter();
        var scaleReadLoop = new ScaleReadLoop(scaleAdapter, fsmLoop, () => false);

        var fsmTask = fsmLoop.RunAsync(cts.Token);
        var orchestratorTask = RunOrchestratorAsync(actionChannel.Reader, orchestrator, cts.Token);
        var printTask = printWorker.RunAsync(cts.Token);
        var erpTask = erpWorker.RunAsync(cts.Token);
        var scaleTask = scaleReadLoop.RunAsync(cts.Token);

        await Task.WhenAll(fsmTask, orchestratorTask, printTask, erpTask, scaleTask);
    }

    private sealed record SimOptions(
        string BaseUrl,
        string Token,
        string DeviceId,
        string BatchId,
        string ProductId);

    private static SimOptions? ParseSimArgs(string[] args)
    {
        string baseUrl = "";
        string token = "";
        string device = "";
        string batch = "";
        string product = "";

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (i + 1 >= args.Length)
            {
                break;
            }

            var value = args[i + 1];
            switch (key)
            {
                case "--baseUrl":
                    baseUrl = value;
                    i++;
                    break;
                case "--token":
                    token = value;
                    i++;
                    break;
                case "--device":
                    device = value;
                    i++;
                    break;
                case "--batch":
                    batch = value;
                    i++;
                    break;
                case "--product":
                    product = value;
                    i++;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(device) ||
            string.IsNullOrWhiteSpace(batch) ||
            string.IsNullOrWhiteSpace(product))
        {
            return null;
        }

        return new SimOptions(baseUrl.TrimEnd('/'), token.Trim(), device.Trim(), batch.Trim(), product.Trim());
    }

    private static async Task RunSimAsync(string[] args)
    {
        var options = ParseSimArgs(args);
        if (options is null)
        {
            Console.WriteLine("Missing args: --baseUrl --token --device --batch --product");
            return;
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"{options.BaseUrl}/")
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("Authorization", $"token {options.Token}");

        static StringContent JsonContent(object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        async Task<(int Status, JsonDocument? Json)> PostAsync(string method, object payload)
        {
            using var content = JsonContent(payload);
            using var response = await client.PostAsync($"api/method/{method}", content);
            var raw = await response.Content.ReadAsStringAsync();
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch
            {
                doc = null;
            }
            return ((int)response.StatusCode, doc);
        }

        static string NewEventId() => Guid.NewGuid().ToString();

        var batchStartId = NewEventId();
        var startPayload = new
        {
            event_id = batchStartId,
            device_id = options.DeviceId,
            batch_id = options.BatchId,
            seq = 0,
            product_id = options.ProductId
        };

        var (startStatus, startJson) = await PostAsync("rfidenter.edge_batch_start", startPayload);
        Console.WriteLine($"batch_start status={startStatus} body={startJson?.RootElement}");

        string? seq25EventId = null;

        for (var seq = 1; seq <= 60; seq++)
        {
            var weight = seq switch
            {
                <= 10 => seq * 0.1,
                <= 40 => 1.0,
                _ => Math.Max(0, 1.0 - ((seq - 40) * 0.05))
            };
            var stable = seq is >= 11 and <= 40;
            var eventId = NewEventId();
            if (seq == 25)
            {
                seq25EventId = eventId;
            }

            var payload = new
            {
                event_id = eventId,
                device_id = options.DeviceId,
                batch_id = options.BatchId,
                seq,
                event_type = "weight",
                payload = new { weight, stable }
            };

            var (status, _) = await PostAsync("rfidenter.edge_event_report", payload);
            if (seq is 1 or 10 or 25 or 40 or 60)
            {
                Console.WriteLine($"event_report seq={seq} status={status}");
            }
        }

        if (!string.IsNullOrEmpty(seq25EventId))
        {
            var dupPayload = new
            {
                event_id = seq25EventId,
                device_id = options.DeviceId,
                batch_id = options.BatchId,
                seq = 25,
                event_type = "weight",
                payload = new { weight = 1.0, stable = true }
            };
            var (dupStatus, dupJson) = await PostAsync("rfidenter.edge_event_report", dupPayload);
            Console.WriteLine($"duplicate seq=25 status={dupStatus} body={dupJson?.RootElement}");
        }

        var conflictPayload = new
        {
            event_id = NewEventId(),
            device_id = options.DeviceId,
            batch_id = options.BatchId,
            seq = 25,
            event_type = "weight",
            payload = new { weight = 1.0, stable = true }
        };
        var (conflictStatus, conflictJson) = await PostAsync("rfidenter.edge_event_report", conflictPayload);
        Console.WriteLine($"seq_conflict status={conflictStatus} body={conflictJson?.RootElement}");

        var regressionPayload = new
        {
            event_id = NewEventId(),
            device_id = options.DeviceId,
            batch_id = options.BatchId,
            seq = 20,
            event_type = "weight",
            payload = new { weight = 0.5, stable = false }
        };
        var (regStatus, regJson) = await PostAsync("rfidenter.edge_event_report", regressionPayload);
        Console.WriteLine($"seq_regression status={regStatus} body={regJson?.RootElement}");

        var statusPayload = new
        {
            event_id = NewEventId(),
            device_id = options.DeviceId,
            batch_id = options.BatchId,
            seq = 61,
            status = "Running"
        };
        var (statusStatus, statusJson) = await PostAsync("rfidenter.device_status", statusPayload);
        Console.WriteLine($"device_status status={statusStatus} body={statusJson?.RootElement}");

        var batchStopId = NewEventId();
        var stopPayload = new
        {
            event_id = batchStopId,
            device_id = options.DeviceId,
            batch_id = options.BatchId,
            seq = 62
        };
        var (stopStatus, stopJson) = await PostAsync("rfidenter.edge_batch_stop", stopPayload);
        Console.WriteLine($"batch_stop status={stopStatus} body={stopJson?.RootElement}");
    }

    private static async Task RunOrchestratorAsync(ChannelReader<FsmAction> reader, EventOrchestrator orchestrator, CancellationToken token)
    {
        await foreach (var action in reader.ReadAllAsync(token))
        {
            await orchestrator.HandleActionAsync(action, token);
        }
    }

    private sealed class NullScaleAdapter : IScaleAdapter
    {
        public async IAsyncEnumerable<WeightSample> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                yield return new WeightSample(0, "kg", NowSeconds());
            }
        }

        private static double NowSeconds()
        {
            return (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private sealed class NullPrinterTransport : IPrinterTransport
    {
        public bool SupportsStatusProbe => false;

        public Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<PrinterStatus> ProbeStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new PrinterStatus(false, false, false, false, true));
        }
    }

    private sealed class NullErpClient : IErpClient
    {
        public Task<ErpResult> PostEventAsync(string payloadJson, CancellationToken cancellationToken)
        {
            return Task.FromResult(ErpResult.Ok);
        }
    }
}
