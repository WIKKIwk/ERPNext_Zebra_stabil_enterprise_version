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

    [Fact]
    public async Task WaitPrintChecksMigrationAddsColumn()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE erp_outbox (
  job_id TEXT PRIMARY KEY,
  event_id TEXT NOT NULL UNIQUE,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  status TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  next_retry_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  last_error TEXT,
  UNIQUE(batch_id, seq)
);
";
            command.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText = @"
INSERT INTO erp_outbox
(job_id, event_id, device_id, batch_id, seq, status, payload_json, payload_hash, attempts, created_at, updated_at)
VALUES
($job_id, $event_id, $device_id, $batch_id, $seq, $status, $payload_json, $payload_hash, $attempts, $created_at, $updated_at);
";
            insert.Parameters.AddWithValue("$job_id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$event_id", "event-1");
            insert.Parameters.AddWithValue("$device_id", "dev-1");
            insert.Parameters.AddWithValue("$batch_id", "batch-1");
            insert.Parameters.AddWithValue("$seq", 1);
            insert.Parameters.AddWithValue("$status", ErpJobStatus.New);
            insert.Parameters.AddWithValue("$payload_json", "{}");
            insert.Parameters.AddWithValue("$payload_hash", "hash");
            insert.Parameters.AddWithValue("$attempts", 0);
            insert.Parameters.AddWithValue("$created_at", 0);
            insert.Parameters.AddWithValue("$updated_at", 0);
            insert.ExecuteNonQuery();
        }

        var erpStore = new ErpOutboxStore(dbPath);
        erpStore.Initialize();

        var job = await erpStore.GetJobAsync("event-1");
        Assert.NotNull(job);
        Assert.Equal(0, job?.WaitPrintChecks ?? -1);
    }
}
