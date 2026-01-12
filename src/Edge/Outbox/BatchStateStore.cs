using Microsoft.Data.Sqlite;

namespace ZebraBridge.Edge.Outbox;

public sealed class BatchStateStore
{
    private readonly string _connectionString;

    public BatchStateStore(string databasePath)
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
CREATE TABLE IF NOT EXISTS batch_state (
  device_id TEXT PRIMARY KEY,
  batch_id TEXT NOT NULL,
  product_id TEXT NOT NULL,
  next_seq INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
";
        command.ExecuteNonQuery();
    }

    public void StartBatch(string deviceId, string batchId, string productId, long nowMs)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO batch_state (device_id, batch_id, product_id, next_seq, updated_at)
VALUES ($device_id, $batch_id, $product_id, 1, $updated_at)
ON CONFLICT(device_id) DO UPDATE
SET batch_id = $batch_id,
    product_id = $product_id,
    next_seq = 1,
    updated_at = $updated_at;
";
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$batch_id", batchId);
        command.Parameters.AddWithValue("$product_id", productId);
        command.Parameters.AddWithValue("$updated_at", nowMs);
        command.ExecuteNonQuery();
    }

    public int GetNextSequence(SqliteConnection connection, SqliteTransaction? transaction, string deviceId)
    {
        using var command = connection.CreateCommand();
        if (transaction != null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = "SELECT next_seq FROM batch_state WHERE device_id = $device_id;";
        command.Parameters.AddWithValue("$device_id", deviceId);
        var result = command.ExecuteScalar();
        if (result is null)
        {
            throw new InvalidOperationException("batch_state missing for device.");
        }

        return Convert.ToInt32(result);
    }

    public void IncrementSequence(SqliteConnection connection, SqliteTransaction? transaction, string deviceId, long nowMs)
    {
        using var command = connection.CreateCommand();
        if (transaction != null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = @"
UPDATE batch_state
SET next_seq = next_seq + 1,
    updated_at = $updated_at
WHERE device_id = $device_id;
";
        command.Parameters.AddWithValue("$updated_at", nowMs);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.ExecuteNonQuery();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
