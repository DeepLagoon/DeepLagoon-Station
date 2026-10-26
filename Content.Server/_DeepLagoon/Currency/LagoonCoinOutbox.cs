using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.Currency;

public sealed record PendingLagoonCoinReward(Guid User, string Key, long Amount, string Reason,
    Guid? Actor = null, long Played = 0, long Bonus = 0, Content.Server.Database.LagoonActivity? Activity = null);

/// <summary>Durable delivery queue; delete only after the game DB commits the idempotent operation.</summary>
public sealed class LagoonCoinOutbox : IDisposable
{
    private readonly SqliteConnection _connection;
    public LagoonCoinOutbox(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS rewards (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT NOT NULL, operation_id TEXT NOT NULL, payload TEXT NOT NULL,
                UNIQUE(user_id, operation_id));
            """;
        command.ExecuteNonQuery();
    }

    public bool Store(PendingLagoonCoinReward reward)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO rewards(user_id,operation_id,payload) VALUES($user,$key,$payload) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("$user", reward.User.ToString());
        command.Parameters.AddWithValue("$key", reward.Key);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(reward));
        return command.ExecuteNonQuery() == 1;
    }

    public IEnumerable<PendingLagoonCoinReward> Load()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM rewards ORDER BY sequence";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return JsonSerializer.Deserialize<PendingLagoonCoinReward>(reader.GetString(0))!;
    }

    public void Complete(PendingLagoonCoinReward reward)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM rewards WHERE user_id=$user AND operation_id=$key";
        command.Parameters.AddWithValue("$user", reward.User.ToString());
        command.Parameters.AddWithValue("$key", reward.Key);
        command.ExecuteNonQuery();
    }
    public void Dispose() => _connection.Dispose();
}
