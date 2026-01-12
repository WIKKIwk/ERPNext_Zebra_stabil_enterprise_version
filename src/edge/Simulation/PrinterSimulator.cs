namespace ZebraBridge.Edge;

public sealed record PrinterSimConfig(
    double ReceivedDelaySeconds,
    double CompletedDelaySeconds,
    bool DropReceived = false,
    bool DropCompleted = false,
    bool Offline = false);

public sealed class PrinterSimulator
{
    private readonly List<ScheduledPrinterEvent> _events = new();

    public PrinterSimConfig Config { get; set; } = new(0.05, 0.20);

    public void Schedule(PrintRequest request, double nowSeconds)
    {
        if (Config.Offline)
        {
            return;
        }

        if (!Config.DropReceived)
        {
            _events.Add(new ScheduledPrinterEvent(nowSeconds + Config.ReceivedDelaySeconds, request.EventId, PrinterAck.Received));
        }

        if (!Config.DropCompleted)
        {
            _events.Add(new ScheduledPrinterEvent(nowSeconds + Config.CompletedDelaySeconds, request.EventId, PrinterAck.Completed));
        }
    }

    public void Pump(double nowSeconds, BatchWeighFsm fsm)
    {
        if (_events.Count == 0)
        {
            return;
        }

        var due = _events.Where(e => e.DueSeconds <= nowSeconds).ToList();
        foreach (var ev in due)
        {
            if (ev.Ack == PrinterAck.Received)
            {
                fsm.OnPrinterReceived(ev.EventId, nowSeconds);
            }
            else
            {
                fsm.OnPrinterCompleted(ev.EventId, nowSeconds);
            }

            _events.Remove(ev);
        }
    }

    private sealed record ScheduledPrinterEvent(double DueSeconds, string EventId, PrinterAck Ack);
}
