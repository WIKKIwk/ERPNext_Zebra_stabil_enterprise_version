using Microsoft.Data.Sqlite;
using ZebraBridge.Edge.Outbox;
using Xunit;

namespace ZebraBridge.Edge.Tests;

public sealed class OutboxTests
{
    [Fact]
    public async Task IdempotencyEnforcedByEventId()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        var batchStore = new BatchStateStore(dbPath);
        var printStore = new PrintOutboxStore(dbPath);
        var erpStore = new ErpOutboxStore(dbPath);

        batchStore.Initialize();
        printStore.Initialize();
        erpStore.Initialize();
        batchStore.StartBatch("dev-1", "batch-1", "prod-1", 0);

        using var connection = batchStore.OpenConnection();
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        var job = new PrintJob(
            Guid.NewGuid().ToString("N"),
            "event-1",
            "dev-1",
            "batch-1",
            1,
            PrintJobStatus.New,
            "{}",
            "hash",
            "STATUS_QUERY",
            0,
            null);

        var ok1 = await printStore.TryInsertAsync(connection, null, job, 0);
        var ok2 = await printStore.TryInsertAsync(connection, null, job, 0);

        using var commit = connection.CreateCommand();
        commit.CommandText = "COMMIT;";
        commit.ExecuteNonQuery();

        Assert.True(ok1);
        Assert.False(ok2);
    }

    [Fact]
    public async Task RestartKeepsJobs()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        var printStore = new PrintOutboxStore(dbPath);
        printStore.Initialize();

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        var job = new PrintJob(
            Guid.NewGuid().ToString("N"),
            "event-1",
            "dev-1",
            "batch-1",
            1,
            PrintJobStatus.New,
            "{}",
            "hash",
            "STATUS_QUERY",
            0,
            null);

        await printStore.TryInsertAsync(connection, null, job, 0);

        using var commit = connection.CreateCommand();
        commit.CommandText = "COMMIT;";
        commit.ExecuteNonQuery();

        var restarted = new PrintOutboxStore(dbPath);
        restarted.Initialize();
        var count = await restarted.CountPendingAsync();

        Assert.Equal(1, count);
    }
}
