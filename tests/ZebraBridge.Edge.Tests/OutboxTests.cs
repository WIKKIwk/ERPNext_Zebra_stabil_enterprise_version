using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class OutboxTests
{
    [Fact]
    public void RestartReplayKeepsIdempotency()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        var store = new OutboxStore(dbPath);
        store.Initialize();
        store.StartBatch("dev-1", "batch-1", "prod-1", 0);

        var job = store.CreateEventAndEnqueue(
            "dev-1",
            "batch-1",
            "{\"payload\":true}",
            "hash",
            CompletionMode.StatusQuery,
            0);

        Assert.Equal(1, store.GetJobCount());

        var restarted = new OutboxStore(dbPath);
        restarted.Initialize();

        var inserted = restarted.TryInsertJobWithEvent(
            Guid.NewGuid().ToString("N"),
            job.EventId,
            job.DeviceId,
            job.BatchId,
            job.Sequence,
            job.PayloadJson,
            job.PayloadHash,
            job.CompletionMode,
            1000);

        Assert.False(inserted);
        Assert.Equal(1, restarted.GetJobCount());
    }
}
