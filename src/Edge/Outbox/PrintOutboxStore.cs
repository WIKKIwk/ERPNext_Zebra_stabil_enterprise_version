using Microsoft.Data.Sqlite;

namespace ZebraBridge.Edge.Outbox;

public sealed record PrintJob(
    string JobId,
    string EventId,
    string DeviceId,
    string BatchId,
    int Sequence,
    string Status,
    string PayloadJson,
    string PayloadHash,
    string CompletionMode,
    int Attempts,
    long? NextRetryAtMs);

public static class PrintJobStatus
{
    public const string New = "NEW";
    public const string Sent = "SENT";
    public const string Received = "RECEIVED";
    public const string Completed = "COMPLETED";
    public const string Done = "DONE";
    public const string Retry = "RETRY";
    public const string Fail = "FAIL";
}

public sealed class PrintOutboxStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public PrintOutboxStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS print_outbox (
  job_id TEXT PRIMARY KEY,
  event_id TEXT NOT NULL UNIQUE,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  status TEXT NOT NULL,
  completion_mode TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  next_retry_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  last_error TEXT,
  UNIQUE(batch_id, seq)
);

CREATE INDEX IF NOT EXISTS idx_print_status_next ON print_outbox(status, next_retry_at);
";
        command.ExecuteNonQuery();
    }

    public async Task<bool> TryInsertAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        PrintJob job,
        long nowMs)
    {
        using var command = connection.CreateCommand();
        if (transaction != null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = @"
INSERT INTO print_outbox
(job_id, event_id, device_id, batch_id, seq, status, completion_mode, payload_json, payload_hash, attempts, created_at, updated_at)
VALUES
($job_id, $event_id, $device_id, $batch_id, $seq, $status, $completion_mode, $payload_json, $payload_hash, $attempts, $created_at, $updated_at);
";
        command.Parameters.AddWithValue("$job_id", job.JobId);
        command.Parameters.AddWithValue("$event_id", job.EventId);
        command.Parameters.AddWithValue("$device_id", job.DeviceId);
        command.Parameters.AddWithValue("$batch_id", job.BatchId);
        command.Parameters.AddWithValue("$seq", job.Sequence);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$completion_mode", job.CompletionMode);
        command.Parameters.AddWithValue("$payload_json", job.PayloadJson);
        command.Parameters.AddWithValue("$payload_hash", job.PayloadHash);
        command.Parameters.AddWithValue("$attempts", job.Attempts);
        command.Parameters.AddWithValue("$created_at", nowMs);
        command.Parameters.AddWithValue("$updated_at", nowMs);
        try
        {
            await command.ExecuteNonQueryAsync();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task<PrintJob?> FetchNextAsync(long nowMs)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT job_id, event_id, device_id, batch_id, seq, status, completion_mode, payload_json, payload_hash, attempts, next_retry_at
FROM print_outbox
WHERE (status = $new OR status = $retry) AND (next_retry_at IS NULL OR next_retry_at <= $now)
ORDER BY created_at
LIMIT 1;
";
            command.Parameters.AddWithValue("$new", PrintJobStatus.New);
            command.Parameters.AddWithValue("$retry", PrintJobStatus.Retry);
            command.Parameters.AddWithValue("$now", nowMs);
            using var reader = await command.ExecuteReaderAsync();
            if (!reader.Read())
            {
                return null;
            }

            var nextRetry = reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10);
            return new PrintJob(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                nextRetry);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task MarkStatusAsync(string eventId, string status, long nowMs)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE print_outbox
SET status = $status,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$updated_at", nowMs);
            command.Parameters.AddWithValue("$event_id", eventId);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task MarkRetryAsync(string eventId, long nextRetryAtMs, string error, long nowMs)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE print_outbox
SET status = $status,
    next_retry_at = $next_retry_at,
    last_error = $last_error,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
            command.Parameters.AddWithValue("$status", PrintJobStatus.Retry);
            command.Parameters.AddWithValue("$next_retry_at", nextRetryAtMs);
            command.Parameters.AddWithValue("$last_error", error);
            command.Parameters.AddWithValue("$updated_at", nowMs);
            command.Parameters.AddWithValue("$event_id", eventId);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpdateCompletionModeAsync(string eventId, string completionMode, long nowMs)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE print_outbox
SET completion_mode = $completion_mode,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
            command.Parameters.AddWithValue("$completion_mode", completionMode);
            command.Parameters.AddWithValue("$updated_at", nowMs);
            command.Parameters.AddWithValue("$event_id", eventId);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<int> CountPendingAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM print_outbox WHERE status != 'DONE';";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        finally
        {
            _mutex.Release();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
