using System.Security.Cryptography;
using Content.Shared._DeepLagoon.PdaMessenger;
using Microsoft.Data.Sqlite;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.PdaMessenger;

internal sealed class PdaMessengerStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public PdaMessengerStore(string path)
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS lines (
                account TEXT NOT NULL,
                profile INTEGER NOT NULL,
                number INTEGER NOT NULL UNIQUE,
                PRIMARY KEY (account, profile)
            );
            CREATE TABLE IF NOT EXISTS contacts (
                owner INTEGER NOT NULL,
                peer INTEGER NOT NULL,
                PRIMARY KEY (owner, peer)
            );
            """;
        command.ExecuteNonQuery();
    }

    public int GetOrCreateNumber(NetUserId user, int profile)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", user.UserId.ToString());
        command.Parameters.AddWithValue("$profile", profile);
        command.CommandText = "SELECT number FROM lines WHERE account = $account AND profile = $profile";
        if (command.ExecuteScalar() is long existing)
        {
            transaction.Commit();
            return (int) existing;
        }

        command.CommandText = "INSERT INTO lines VALUES ($account, $profile, $number) ON CONFLICT(number) DO NOTHING";
        var parameter = command.Parameters.Add("$number", SqliteType.Integer);
        int number;
        do
        {
            number = RandomNumberGenerator.GetInt32(PdaMessengerLimits.FirstNumber, PdaMessengerLimits.FirstTemporaryNumber);
            parameter.Value = number;
        } while (command.ExecuteNonQuery() == 0);

        transaction.Commit();
        return number;
    }

    public HashSet<int> LoadContacts(int owner)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT peer FROM contacts WHERE owner = $owner";
        command.Parameters.AddWithValue("$owner", owner);
        using var reader = command.ExecuteReader();
        var contacts = new HashSet<int>();
        while (reader.Read())
            contacts.Add(reader.GetInt32(0));
        return contacts;
    }

    public void SetContact(int owner, int peer, bool saved)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = saved
            ? "INSERT INTO contacts VALUES ($owner, $peer) ON CONFLICT DO NOTHING"
            : "DELETE FROM contacts WHERE owner = $owner AND peer = $peer";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$peer", peer);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
