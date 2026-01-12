using System.Threading.Channels;
using ZebraBridge.Edge;
using ZebraBridge.Edge.Fsm;

namespace ZebraBridge.Edge.Runtime;

public sealed class FsmEventLoop
{
    private readonly IFsmHandler _fsm;
    private readonly ChannelWriter<FsmAction> _actionWriter;
    private readonly SemaphoreSlim _signal;
    private readonly Queue<FsmEvent> _controlQueue = new();
    private readonly object _controlLock = new();
    private readonly int _capacity;
    private readonly IAlarmSink _alarmSink;
    private int _overflowFlag;
    private int _controlSignalPending;
    private int _sampleSignalPending;
    private int _overflowSignaled;
    private long _sampleWakeups;
    private SampleEvent? _latestSample;

    public FsmEventLoop(
        BatchWeighFsm fsm,
        ChannelWriter<FsmAction> actionWriter,
        int controlCapacity,
        SemaphoreSlim signal,
        IAlarmSink alarmSink)
        : this(new BatchWeighFsmAdapter(fsm), actionWriter, controlCapacity, signal, alarmSink)
    {
    }

    public FsmEventLoop(
        IFsmHandler fsm,
        ChannelWriter<FsmAction> actionWriter,
        int controlCapacity,
        SemaphoreSlim signal,
        IAlarmSink alarmSink)
    {
        _fsm = fsm;
        _actionWriter = actionWriter;
        _capacity = controlCapacity;
        _signal = signal;
        _alarmSink = alarmSink;
    }

    public bool EnqueueControl(FsmEvent ev)
    {
        bool enqueued;
        lock (_controlLock)
        {
            if (_controlQueue.Count >= _capacity)
            {
                enqueued = false;
            }
            else
            {
                _controlQueue.Enqueue(ev);
                enqueued = true;
            }
        }

        if (!enqueued)
        {
            Interlocked.Exchange(ref _overflowFlag, 1);
            _alarmSink.Raise("CONTROL_QUEUE_OVERFLOW");
            if (Interlocked.Exchange(ref _overflowSignaled, 1) == 0)
            {
                _signal.Release();
            }
        }

        if (enqueued)
        {
            if (Interlocked.Exchange(ref _controlSignalPending, 1) == 0)
            {
                _signal.Release();
            }
        }
        return enqueued;
    }

    public void UpdateLatestSample(WeightSample sample)
    {
        Interlocked.Exchange(ref _latestSample, new SampleEvent(sample));
        if (Interlocked.Exchange(ref _sampleSignalPending, 1) == 0)
        {
            Interlocked.Increment(ref _sampleWakeups);
            _signal.Release();
        }
    }

    public long SampleWakeups => Interlocked.Read(ref _sampleWakeups);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken);

            if (Interlocked.Exchange(ref _overflowFlag, 0) == 1)
            {
                Dispatch(new PauseEvent(PauseReason.ControlQueueOverflow, GetTimestamp()));
                Interlocked.Exchange(ref _overflowSignaled, 0);
                continue;
            }

            bool handled = false;
            while (TryDequeueControl(out var control))
            {
                Dispatch(control);
                handled = true;
            }

            Interlocked.Exchange(ref _controlSignalPending, 0);

            if (!handled)
            {
                var sample = Interlocked.Exchange(ref _latestSample, null);
                if (sample != null)
                {
                    Dispatch(sample);
                    Interlocked.Exchange(ref _sampleSignalPending, 0);
                    if (Volatile.Read(ref _latestSample) != null)
                    {
                        if (Interlocked.Exchange(ref _sampleSignalPending, 1) == 0)
                        {
                            Interlocked.Increment(ref _sampleWakeups);
                            _signal.Release();
                        }
                    }
                }
            }
        }
    }

    private void Dispatch(FsmEvent ev)
    {
        var actions = _fsm.Handle(ev);
        foreach (var action in actions)
        {
            _actionWriter.TryWrite(action);
        }
    }

    private bool TryDequeueControl(out FsmEvent ev)
    {
        lock (_controlLock)
        {
            if (_controlQueue.Count == 0)
            {
                ev = null!;
                return false;
            }

            ev = _controlQueue.Dequeue();
            return true;
        }
    }

    private static double GetTimestamp()
    {
        return (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
    }
}

public interface IAlarmSink
{
    void Raise(string code);
}

public sealed class NullAlarmSink : IAlarmSink
{
    public void Raise(string code)
    {
    }
}

public interface IFsmHandler
{
    IReadOnlyList<FsmAction> Handle(FsmEvent ev);
}

file sealed class BatchWeighFsmAdapter : IFsmHandler
{
    private readonly BatchWeighFsm _fsm;

    public BatchWeighFsmAdapter(BatchWeighFsm fsm)
    {
        _fsm = fsm;
    }

    public IReadOnlyList<FsmAction> Handle(FsmEvent ev)
    {
        return _fsm.Handle(ev);
    }
}
