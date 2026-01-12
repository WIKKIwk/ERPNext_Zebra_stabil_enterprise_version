using Microsoft.Data.Sqlite;

namespace ZebraBridge.Edge;

public sealed class OutboxStore
{
    private readonly string _connectionString;

    public OutboxStore(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        };
        _connectionString = builder.ToString();
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS batch_state (
  device_id TEXT PRIMARY KEY,
  batch_id TEXT NOT NULL,
  product_id TEXT NOT NULL,
  next_seq INTEGER NOT NULL,
  status TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS batch_runs (
  run_id TEXT PRIMARY KEY,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  product_id TEXT NOT NULL,
  started_at INTEGER NOT NULL,
  stopped_at INTEGER,
  stop_reason TEXT,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS print_jobs (
  job_id TEXT PRIMARY KEY,
  event_id TEXT NOT NULL UNIQUE,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  status TEXT NOT NULL,
  completion_mode TEXT NOT NULL DEFAULT 'STATUS_QUERY',
  payload_json TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  lease_expires_at INTEGER,
  next_retry_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  last_error TEXT,
  UNIQUE(batch_id, seq)
);

CREATE INDEX IF NOT EXISTS idx_jobs_status_next ON print_jobs(status, next_retry_at);
";
        command.ExecuteNonQuery();
    }

    public void StartBatch(string deviceId, string batchId, string productId, long nowMs)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO batch_state (device_id, batch_id, product_id, next_seq, status, updated_at)
VALUES ($device_id, $batch_id, $product_id, 1, 'ACTIVE', $updated_at)
ON CONFLICT(device_id) DO UPDATE
SET batch_id = $batch_id,
    product_id = $product_id,
    next_seq = 1,
    status = 'ACTIVE',
    updated_at = $updated_at;
";
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$batch_id", batchId);
        command.Parameters.AddWithValue("$product_id", productId);
        command.Parameters.AddWithValue("$updated_at", nowMs);
        command.ExecuteNonQuery();

        var runId = Guid.NewGuid().ToString("N");
        using var runCommand = connection.CreateCommand();
        runCommand.CommandText = @"
INSERT INTO batch_runs (run_id, device_id, batch_id, product_id, started_at, created_at)
VALUES ($run_id, $device_id, $batch_id, $product_id, $started_at, $created_at);
";
        runCommand.Parameters.AddWithValue("$run_id", runId);
        runCommand.Parameters.AddWithValue("$device_id", deviceId);
        runCommand.Parameters.AddWithValue("$batch_id", batchId);
        runCommand.Parameters.AddWithValue("$product_id", productId);
        runCommand.Parameters.AddWithValue("$started_at", nowMs);
        runCommand.Parameters.AddWithValue("$created_at", nowMs);
        runCommand.ExecuteNonQuery();
    }

    public void StopBatch(string deviceId, string batchId, string reason, long nowMs)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE batch_runs
SET stopped_at = $stopped_at,
    stop_reason = $stop_reason
WHERE device_id = $device_id AND batch_id = $batch_id AND stopped_at IS NULL;
";
        command.Parameters.AddWithValue("$stopped_at", nowMs);
        command.Parameters.AddWithValue("$stop_reason", reason);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$batch_id", batchId);
        command.ExecuteNonQuery();
    }

    public OutboxJob CreateEventAndEnqueue(
        string deviceId,
        string batchId,
        string payloadJson,
        string payloadHash,
        CompletionMode completionMode,
        long nowMs)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var seqCmd = connection.CreateCommand();
        seqCmd.Transaction = transaction;
        seqCmd.CommandText = "SELECT next_seq FROM batch_state WHERE device_id = $device_id;";
        seqCmd.Parameters.AddWithValue("$device_id", deviceId);
        var result = seqCmd.ExecuteScalar();
        if (result is null)
        {
            throw new InvalidOperationException("batch_state missing for device.");
        }

        var seq = Convert.ToInt32(result);

        var updateCmd = connection.CreateCommand();
        updateCmd.Transaction = transaction;
        updateCmd.CommandText = @"
UPDATE batch_state
SET next_seq = next_seq + 1,
    updated_at = $updated_at
WHERE device_id = $device_id;
";
        updateCmd.Parameters.AddWithValue("$updated_at", nowMs);
        updateCmd.Parameters.AddWithValue("$device_id", deviceId);
        updateCmd.ExecuteNonQuery();

        var jobId = Guid.NewGuid().ToString("N");
        var eventId = Guid.NewGuid().ToString("N");
        var status = OutboxStatus.New;

        var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = @"
INSERT INTO print_jobs
(job_id, event_id, device_id, batch_id, seq, status, completion_mode, payload_json, payload_hash, created_at, updated_at)
VALUES
($job_id, $event_id, $device_id, $batch_id, $seq, $status, $completion_mode, $payload_json, $payload_hash, $created_at, $updated_at);
";
        insertCmd.Parameters.AddWithValue("$job_id", jobId);
        insertCmd.Parameters.AddWithValue("$event_id", eventId);
        insertCmd.Parameters.AddWithValue("$device_id", deviceId);
        insertCmd.Parameters.AddWithValue("$batch_id", batchId);
        insertCmd.Parameters.AddWithValue("$seq", seq);
        insertCmd.Parameters.AddWithValue("$status", status);
        insertCmd.Parameters.AddWithValue("$completion_mode", CompletionModeToString(completionMode));
        insertCmd.Parameters.AddWithValue("$payload_json", payloadJson);
        insertCmd.Parameters.AddWithValue("$payload_hash", payloadHash);
        insertCmd.Parameters.AddWithValue("$created_at", nowMs);
        insertCmd.Parameters.AddWithValue("$updated_at", nowMs);
        insertCmd.ExecuteNonQuery();

        transaction.Commit();
        return new OutboxJob(jobId, eventId, deviceId, batchId, seq, status, payloadJson, payloadHash, completionMode);
    }

    public bool TryInsertJobWithEvent(
        string jobId,
        string eventId,
        string deviceId,
        string batchId,
        int seq,
        string payloadJson,
        string payloadHash,
        CompletionMode completionMode,
        long nowMs)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO print_jobs
(job_id, event_id, device_id, batch_id, seq, status, completion_mode, payload_json, payload_hash, created_at, updated_at)
VALUES
($job_id, $event_id, $device_id, $batch_id, $seq, $status, $completion_mode, $payload_json, $payload_hash, $created_at, $updated_at);
";
            command.Parameters.AddWithValue("$job_id", jobId);
            command.Parameters.AddWithValue("$event_id", eventId);
            command.Parameters.AddWithValue("$device_id", deviceId);
            command.Parameters.AddWithValue("$batch_id", batchId);
            command.Parameters.AddWithValue("$seq", seq);
            command.Parameters.AddWithValue("$status", OutboxStatus.New);
            command.Parameters.AddWithValue("$completion_mode", CompletionModeToString(completionMode));
            command.Parameters.AddWithValue("$payload_json", payloadJson);
            command.Parameters.AddWithValue("$payload_hash", payloadHash);
            command.Parameters.AddWithValue("$created_at", nowMs);
            command.Parameters.AddWithValue("$updated_at", nowMs);
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public OutboxJob? GetJobByEventId(string eventId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT job_id, event_id, device_id, batch_id, seq, status, completion_mode, payload_json, payload_hash
FROM print_jobs
WHERE event_id = $event_id;
";
        command.Parameters.AddWithValue("$event_id", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var completionMode = CompletionModeFromString(reader.GetString(6));
        return new OutboxJob(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(7),
            reader.GetString(8),
            completionMode);
    }

    public int GetJobCount()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM print_jobs;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void MarkReceived(string eventId, long nowMs)
    {
        UpdateStatus(eventId, OutboxStatus.Received, nowMs);
    }

    public void MarkCompleted(string eventId, long nowMs)
    {
        UpdateStatus(eventId, OutboxStatus.Completed, nowMs);
    }

    public void MarkDone(string eventId, long nowMs)
    {
        UpdateStatus(eventId, OutboxStatus.Done, nowMs);
    }

    public void MarkRetry(string eventId, long nextRetryAtMs, string? error, long nowMs)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE print_jobs
SET status = $status,
    next_retry_at = $next_retry_at,
    last_error = $last_error,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
        command.Parameters.AddWithValue("$status", OutboxStatus.Retry);
        command.Parameters.AddWithValue("$next_retry_at", nextRetryAtMs);
        command.Parameters.AddWithValue("$last_error", error ?? string.Empty);
        command.Parameters.AddWithValue("$updated_at", nowMs);
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
    }

    private void UpdateStatus(string eventId, string status, long nowMs)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE print_jobs
SET status = $status,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated_at", nowMs);
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string CompletionModeToString(CompletionMode mode)
    {
        return mode == CompletionMode.StatusQuery ? "STATUS_QUERY" : "SCAN_RECON";
    }

    private static CompletionMode CompletionModeFromString(string value)
    {
        return value.Equals("SCAN_RECON", StringComparison.OrdinalIgnoreCase)
            ? CompletionMode.ScanRecon
            : CompletionMode.StatusQuery;
    }
}
