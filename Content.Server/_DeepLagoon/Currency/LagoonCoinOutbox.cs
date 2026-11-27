using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Persistence;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.Currency;

public sealed record PendingLagoonCoinReward(Guid User, string Key, long Amount, string Reason,
    Guid? Actor = null, long Played = 0, long Bonus = 0, Content.Server.Database.LagoonActivity? Activity = null);

/// <summary>Durable delivery queue; delete only after the game DB commits the idempotent operation.</summary>
public sealed class LagoonCoinOutbox : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SerialPersistenceQueue _writer = new();
    public int PendingWrites => _writer.PendingCount;
    public LagoonCoinOutbox(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
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

    public bool Store(PendingLagoonCoinReward reward) => StoreAsync(reward).GetAwaiter().GetResult();
    public Task<bool> StoreAsync(PendingLagoonCoinReward reward) => _writer.Enqueue(() => StoreCore(reward));
    private bool StoreCore(PendingLagoonCoinReward reward)
    {
        using var command = CreateInsert();
        return Insert(command, reward);
    }

    public bool[] StoreBatch(IReadOnlyList<PendingLagoonCoinReward> rewards) => StoreBatchAsync(rewards).GetAwaiter().GetResult();
    public Task<bool[]> StoreBatchAsync(IReadOnlyList<PendingLagoonCoinReward> rewards)
    {
        var owned = rewards.ToArray();
        return _writer.Enqueue(() => StoreBatchCore(owned));
    }
    private bool[] StoreBatchCore(IReadOnlyList<PendingLagoonCoinReward> rewards)
    {
        var inserted = new bool[rewards.Count];
        using var transaction = _connection.BeginTransaction();
        using var command = CreateInsert();
        command.Transaction = transaction;
        for (var i = 0; i < rewards.Count; i++)
            inserted[i] = Insert(command, rewards[i]);

        transaction.Commit();
        return inserted;
    }

    private SqliteCommand CreateInsert()
    {
        var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO rewards(user_id,operation_id,payload) VALUES($user,$key,$payload) ON CONFLICT DO NOTHING";
        command.Parameters.Add("$user", SqliteType.Text);
        command.Parameters.Add("$key", SqliteType.Text);
        command.Parameters.Add("$payload", SqliteType.Text);
        return command;
    }

    private static bool Insert(SqliteCommand command, PendingLagoonCoinReward reward)
    {
        command.Parameters[0].Value = reward.User.ToString();
        command.Parameters[1].Value = reward.Key;
        command.Parameters[2].Value = JsonSerializer.Serialize(reward);
        return command.ExecuteNonQuery() == 1;
    }

    public IEnumerable<PendingLagoonCoinReward> Load() => _writer.Enqueue(() => LoadCore().ToArray()).GetAwaiter().GetResult();
    private IEnumerable<PendingLagoonCoinReward> LoadCore()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM rewards ORDER BY sequence";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return JsonSerializer.Deserialize<PendingLagoonCoinReward>(reader.GetString(0))!;
    }

    public void Complete(PendingLagoonCoinReward reward) => CompleteAsync(reward).GetAwaiter().GetResult();
    public Task CompleteAsync(PendingLagoonCoinReward reward) => _writer.Enqueue(() => CompleteCore(reward));
    private void CompleteCore(PendingLagoonCoinReward reward)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM rewards WHERE user_id=$user AND operation_id=$key";
        command.Parameters.AddWithValue("$user", reward.User.ToString());
        command.Parameters.AddWithValue("$key", reward.Key);
        command.ExecuteNonQuery();
    }
    public Task FlushAsync() => _writer.FlushAsync();
    public void Dispose() { _writer.Dispose(); _connection.Dispose(); }
}
