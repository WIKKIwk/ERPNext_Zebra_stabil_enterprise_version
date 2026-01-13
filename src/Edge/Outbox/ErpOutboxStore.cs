using Microsoft.Data.Sqlite;

namespace ZebraBridge.Edge.Outbox;

public sealed record ErpJob(
    string JobId,
    string EventId,
    string DeviceId,
    string BatchId,
    int Sequence,
    string Status,
    string PayloadJson,
    string PayloadHash,
    int Attempts,
    int WaitPrintChecks,
    long CreatedAtMs,
    long? NextRetryAtMs);

public static class ErpJobStatus
{
    public const string New = "NEW";
    public const string Sent = "SENT";
    public const string Done = "DONE";
    public const string Retry = "RETRY";
    public const string Fail = "FAIL";
    public const string NeedsOperator = "NEEDS_OPERATOR";
}

public sealed class ErpOutboxStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public ErpOutboxStore(string databasePath)
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
CREATE TABLE IF NOT EXISTS erp_outbox (
  job_id TEXT PRIMARY KEY,
  event_id TEXT NOT NULL UNIQUE,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  status TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  wait_print_checks INTEGER NOT NULL DEFAULT 0,
  next_retry_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  last_error TEXT,
  UNIQUE(batch_id, seq)
);

CREATE INDEX IF NOT EXISTS idx_erp_status_next ON erp_outbox(status, next_retry_at);
";
        command.ExecuteNonQuery();

        EnsureColumn(connection, "wait_print_checks", "INTEGER NOT NULL DEFAULT 0");
    }

    public async Task<bool> TryInsertAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ErpJob job,
        long nowMs)
    {
        using var command = connection.CreateCommand();
        if (transaction != null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = @"
INSERT INTO erp_outbox
(job_id, event_id, device_id, batch_id, seq, status, payload_json, payload_hash, attempts, wait_print_checks, created_at, updated_at)
VALUES
($job_id, $event_id, $device_id, $batch_id, $seq, $status, $payload_json, $payload_hash, $attempts, $wait_print_checks, $created_at, $updated_at);
";
        command.Parameters.AddWithValue("$job_id", job.JobId);
        command.Parameters.AddWithValue("$event_id", job.EventId);
        command.Parameters.AddWithValue("$device_id", job.DeviceId);
        command.Parameters.AddWithValue("$batch_id", job.BatchId);
        command.Parameters.AddWithValue("$seq", job.Sequence);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$payload_json", job.PayloadJson);
        command.Parameters.AddWithValue("$payload_hash", job.PayloadHash);
        command.Parameters.AddWithValue("$attempts", job.Attempts);
        command.Parameters.AddWithValue("$wait_print_checks", job.WaitPrintChecks);
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

    public async Task<ErpJob?> FetchNextAsync(long nowMs)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT job_id, event_id, device_id, batch_id, seq, status, payload_json, payload_hash, attempts, wait_print_checks, created_at, next_retry_at
FROM erp_outbox
WHERE (status = $new OR status = $retry) AND (next_retry_at IS NULL OR next_retry_at <= $now)
ORDER BY created_at
LIMIT 1;
";
            command.Parameters.AddWithValue("$new", ErpJobStatus.New);
            command.Parameters.AddWithValue("$retry", ErpJobStatus.Retry);
            command.Parameters.AddWithValue("$now", nowMs);
            using var reader = await command.ExecuteReaderAsync();
            if (!reader.Read())
            {
                return null;
            }

            var waitChecks = reader.GetInt32(9);
            var createdAt = reader.GetInt64(10);
            var nextRetry = reader.IsDBNull(11) ? (long?)null : reader.GetInt64(11);
            return new ErpJob(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                waitChecks,
                createdAt,
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
UPDATE erp_outbox
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
UPDATE erp_outbox
SET status = $status,
    attempts = attempts + 1,
    next_retry_at = $next_retry_at,
    last_error = $last_error,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
            command.Parameters.AddWithValue("$status", ErpJobStatus.Retry);
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

    public async Task MarkWaitPrintAsync(string eventId, long nextRetryAtMs, long nowMs)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE erp_outbox
SET status = $status,
    wait_print_checks = wait_print_checks + 1,
    next_retry_at = $next_retry_at,
    last_error = $last_error,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
            command.Parameters.AddWithValue("$status", ErpJobStatus.Retry);
            command.Parameters.AddWithValue("$next_retry_at", nextRetryAtMs);
            command.Parameters.AddWithValue("$last_error", "WAIT_PRINT");
            command.Parameters.AddWithValue("$updated_at", nowMs);
            command.Parameters.AddWithValue("$event_id", eventId);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ErpJob?> GetJobAsync(string eventId)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT job_id, event_id, device_id, batch_id, seq, status, payload_json, payload_hash, attempts, wait_print_checks, created_at, next_retry_at
FROM erp_outbox
WHERE event_id = $event_id;
";
            command.Parameters.AddWithValue("$event_id", eventId);
            using var reader = await command.ExecuteReaderAsync();
            if (!reader.Read())
            {
                return null;
            }

            var waitChecks = reader.GetInt32(9);
            var createdAt = reader.GetInt64(10);
            var nextRetry = reader.IsDBNull(11) ? (long?)null : reader.GetInt64(11);
            return new ErpJob(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                waitChecks,
                createdAt,
                nextRetry);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task MarkNeedsOperatorAsync(string eventId, string error, long nowMs)
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE erp_outbox
SET status = $status,
    last_error = $last_error,
    updated_at = $updated_at
WHERE event_id = $event_id;
";
            command.Parameters.AddWithValue("$status", ErpJobStatus.NeedsOperator);
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

    public async Task<int> CountPendingAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM erp_outbox WHERE status != 'DONE';";
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

    private static void EnsureColumn(SqliteConnection connection, string columnName, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(erp_outbox);";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE erp_outbox ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }
}
