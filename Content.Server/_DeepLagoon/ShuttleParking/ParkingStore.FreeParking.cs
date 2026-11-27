using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed record FreeParkingSnapshot(Guid Identity, Guid Owner, int Character, Guid Revision,
    string Name, string OwnerName, int Category, int OriginalPrice, string Checksum, int Profile = 0, string? OriginFaction = null, string? Vessel = null);
public sealed record FreeParkingRead(string Grid, Guid[] Assets);

public sealed partial class ParkingStore
{
    private string _freeParkingDirectory = "";
    private readonly SemaphoreSlim _freeParkingWriter = new(1, 1);
    private FreeParkingSnapshot? _freeParkingClaim;

    private void InitializeFreeParking(string path)
    {
        _freeParkingDirectory = path == ":memory:" ? "" : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "free-parking");
        Execute("""
            CREATE TABLE IF NOT EXISTS free_parking(
              identity TEXT PRIMARY KEY, owner TEXT NOT NULL, character INTEGER NOT NULL,
              revision TEXT NOT NULL, phase TEXT NOT NULL, metadata TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS free_parking_owner ON free_parking(owner,character);
            CREATE TABLE IF NOT EXISTS free_parking_pending(identity TEXT PRIMARY KEY, revision TEXT NOT NULL, metadata TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS free_parking_assets(
              identity TEXT NOT NULL, asset TEXT NOT NULL, revision TEXT NOT NULL, PRIMARY KEY(identity,asset));
            CREATE INDEX IF NOT EXISTS free_parking_asset ON free_parking_assets(asset);
            UPDATE free_parking SET phase='stored' WHERE phase IN ('claiming','changing');
            DELETE FROM free_parking_pending;
            """);
    }

    private SqliteConnection FreeParkingConnection()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var settings = db.CreateCommand();
        settings.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        settings.ExecuteNonQuery();
        return db;
    }

    private async Task<T> FreeParkingWrite<T>(Func<SqliteConnection, T> write)
    {
        await _freeParkingWriter.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => { using var db = FreeParkingConnection(); return write(db); }).ConfigureAwait(false); }
        finally { _freeParkingWriter.Release(); }
    }

    public Task<FreeParkingSnapshot[]> FreeParkingIndexAsync() => Task.Run(() =>
    {
        using var db = FreeParkingConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT metadata FROM free_parking WHERE phase='stored'";
        using var reader = command.ExecuteReader();
        var snapshots = new List<FreeParkingSnapshot>();
        while (reader.Read()) snapshots.Add(JsonSerializer.Deserialize<FreeParkingSnapshot>(reader.GetString(0))!);
        return snapshots.ToArray();
    });

    public Task BeginFreeParkingAsync(FreeParkingSnapshot snapshot) => FreeParkingWrite(db =>
    {
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO free_parking_pending(identity,revision,metadata)
            VALUES($id,$revision,$metadata)
            ON CONFLICT(identity) DO UPDATE SET revision=$revision,metadata=$metadata;
            """;
        command.Parameters.AddWithValue("$id", snapshot.Identity.ToString());
        command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(snapshot));
        command.ExecuteNonQuery(); transaction.Commit(); return true;
    });

    public Task<bool> CommitFreeParkingAsync(FreeParkingSnapshot snapshot, string yaml, Guid[] assets) => FreeParkingWrite(db =>
    {
        if (_freeParkingDirectory.Length == 0) throw new InvalidOperationException("Persistent free parking requires a data directory.");
        if (yaml.Length > 32_000_000) throw new InvalidDataException("Free-parking snapshot exceeds the maximum size.");
        snapshot = snapshot with { Checksum = Hash(yaml) };
        using (var check = db.CreateCommand())
        {
            check.CommandText = """
                SELECT metadata,'pending' FROM free_parking_pending WHERE identity=$id AND revision=$revision
                UNION ALL SELECT metadata,phase FROM free_parking WHERE identity=$id AND revision=$revision
                """;
            check.Parameters.AddWithValue("$id", snapshot.Identity.ToString()); check.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
            using var reader = check.ExecuteReader();
            if (!reader.Read()) return false;
            var current = JsonSerializer.Deserialize<FreeParkingSnapshot>(reader.GetString(0))!;
            if (current.Owner != snapshot.Owner || current.Character != snapshot.Character || current.Profile != snapshot.Profile || current.OriginFaction != snapshot.OriginFaction) return false;
            if (reader.GetString(1) == "stored") return current.Checksum == snapshot.Checksum;
            if (reader.GetString(1) != "pending") return false;
        }
        FreeParkingSnapshot? previous = null;
        using (var old = db.CreateCommand())
        {
            old.CommandText = "SELECT metadata FROM free_parking WHERE identity=$id";
            old.Parameters.AddWithValue("$id", snapshot.Identity.ToString());
            if (old.ExecuteScalar() is string json) previous = JsonSerializer.Deserialize<FreeParkingSnapshot>(json);
        }
        Directory.CreateDirectory(_freeParkingDirectory);
        var file = FreeParkingFile(snapshot);
        var temporary = file + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(yaml);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO free_parking(identity,owner,character,revision,phase,metadata)
                SELECT $id,$owner,$character,$revision,'stored',$metadata
                WHERE EXISTS(SELECT 1 FROM free_parking_pending WHERE identity=$id AND revision=$revision)
                ON CONFLICT(identity) DO UPDATE SET owner=$owner,character=$character,revision=$revision,phase='stored',metadata=$metadata
                """;
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(snapshot));
            command.Parameters.AddWithValue("$id", snapshot.Identity.ToString());
            command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
            command.Parameters.AddWithValue("$owner", snapshot.Owner.ToString());
            command.Parameters.AddWithValue("$character", snapshot.Character);
            if (command.ExecuteNonQuery() != 1) { transaction.Rollback(); File.Delete(file); return false; }
            command.CommandText = "DELETE FROM free_parking_assets WHERE identity=$id; DELETE FROM free_parking_pending WHERE identity=$id AND revision=$revision";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO free_parking_assets(identity,asset,revision) VALUES($id,$asset,$revision)";
            command.Parameters.Add("$asset", SqliteType.Text);
            foreach (var asset in assets)
            {
                command.Parameters["$asset"].Value = asset.ToString(); command.ExecuteNonQuery();
            }
            transaction.Commit();
            if (previous != null && previous.Revision != snapshot.Revision) File.Delete(FreeParkingFile(previous));
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    });

    public Task InvalidateFreeParkingAsync(Guid identity, Guid revision) => InvalidateFreeParkingAsync([(identity, revision)]);

    public Task<FreeParkingSnapshot?> SuspendFreeParkingAsync(Guid identity, Guid pendingRevision) => FreeParkingWrite(db =>
    {
        using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", identity.ToString()); command.Parameters.AddWithValue("$revision", pendingRevision.ToString());
        command.CommandText = "DELETE FROM free_parking_pending WHERE identity=$id AND revision=$revision; UPDATE free_parking SET phase='changing' WHERE identity=$id AND phase='stored'";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT metadata FROM free_parking WHERE identity=$id AND phase IN ('stored','changing')";
        var result = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<FreeParkingSnapshot>(json) : null;
        transaction.Commit(); return result;
    });

    /// <summary>A durable claim keeps the last complete copy until publication. Restart rolls it back to stored.</summary>
    public async Task<bool> ClaimFreeParkingAsync(FreeParkingSnapshot snapshot)
    {
        await _freeParkingWriter.WaitAsync().ConfigureAwait(false);
        var claimed = false;
        try
        {
            claimed = await Task.Run(() =>
            {
                using var db = FreeParkingConnection(); using var command = db.CreateCommand();
                command.CommandText = "UPDATE free_parking SET phase='claiming' WHERE identity=$id AND revision=$revision AND owner=$owner AND character=$character AND phase IN ('stored','changing') AND json_extract(metadata,'$.Profile')=$profile AND json_extract(metadata,'$.OriginFaction') IS $faction";
                command.Parameters.AddWithValue("$id", snapshot.Identity.ToString()); command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
                command.Parameters.AddWithValue("$owner", snapshot.Owner.ToString()); command.Parameters.AddWithValue("$character", snapshot.Character);
                command.Parameters.AddWithValue("$profile", snapshot.Profile);
                command.Parameters.AddWithValue("$faction", (object?)snapshot.OriginFaction ?? DBNull.Value);
                return command.ExecuteNonQuery() == 1;
            }).ConfigureAwait(false);
            if (claimed) _freeParkingClaim = snapshot;
            return claimed;
        }
        finally { if (!claimed) _freeParkingWriter.Release(); }
    }

    /// <summary>
    /// Tiny synchronous publication fence, once under the global recall gate: commit after moving the private
    /// grid but before the next tick can expose it to players. YAML/file I/O stays on the worker.
    /// </summary>
    public Task CompleteFreeParkingClaim(FreeParkingSnapshot snapshot)
    {
        if (_freeParkingClaim?.Identity != snapshot.Identity || _freeParkingClaim.Revision != snapshot.Revision)
            throw new InvalidOperationException("Free-parking claim does not belong to this operation.");
        using (var db = FreeParkingConnection())
        using (var transaction = db.BeginTransaction())
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", snapshot.Identity.ToString()); command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
            command.CommandText = "DELETE FROM free_parking WHERE identity=$id AND revision=$revision AND phase='claiming'";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Free-parking claim changed.");
            command.CommandText = "DELETE FROM free_parking_assets WHERE identity=$id AND revision=$revision; DELETE FROM free_parking_pending WHERE identity=$id;";
            command.ExecuteNonQuery(); transaction.Commit();
        }
        _freeParkingClaim = null; _freeParkingWriter.Release();
        var cleanup = Task.Run(() => { if (_freeParkingDirectory.Length > 0) File.Delete(FreeParkingFile(snapshot)); });
        _ = cleanup.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return cleanup;
    }

    public Task AbortAnyFreeParkingClaimAsync() => _freeParkingClaim is { } snapshot ? AbortFreeParkingClaimAsync(snapshot) : Task.CompletedTask;

    public async Task AbortFreeParkingClaimAsync(FreeParkingSnapshot snapshot)
    {
        var claim = _freeParkingClaim;
        if (claim?.Identity != snapshot.Identity || claim.Revision != snapshot.Revision ||
            Interlocked.CompareExchange(ref _freeParkingClaim, null, claim) != claim) return;
        try
        {
            await Task.Run(() =>
            {
                using var db = FreeParkingConnection(); using var command = db.CreateCommand();
                command.CommandText = "UPDATE free_parking SET phase='stored' WHERE identity=$id AND revision=$revision AND phase='claiming'";
                command.Parameters.AddWithValue("$id", snapshot.Identity.ToString()); command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
                command.ExecuteNonQuery();
            }).ConfigureAwait(false);
        }
        finally { _freeParkingWriter.Release(); }
    }

    public Task InvalidateFreeParkingAsync((Guid Identity, Guid Revision)[] snapshots) => FreeParkingWrite(db =>
    {
        var removed = new List<FreeParkingSnapshot>();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.Parameters.Add("$id", SqliteType.Text); command.Parameters.Add("$revision", SqliteType.Text);
        foreach (var (identity, revision) in snapshots)
        {
            command.Parameters["$id"].Value = identity.ToString(); command.Parameters["$revision"].Value = revision.ToString();
            command.CommandText = "SELECT metadata FROM free_parking WHERE identity=$id AND revision=$revision";
            if (command.ExecuteScalar() is string json) removed.Add(JsonSerializer.Deserialize<FreeParkingSnapshot>(json)!);
            command.CommandText = "DELETE FROM free_parking_assets WHERE identity=$id AND revision=$revision; DELETE FROM free_parking WHERE identity=$id AND revision=$revision; DELETE FROM free_parking_pending WHERE identity=$id AND revision=$revision;";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        if (_freeParkingDirectory.Length > 0)
            foreach (var snapshot in removed) File.Delete(FreeParkingFile(snapshot));
        return true;
    });

    public Task<FreeParkingRead> ReadFreeParkingAsync(FreeParkingSnapshot snapshot) => Task.Run(() =>
    {
        using var db = FreeParkingConnection(); using var command = db.CreateCommand();
        command.CommandText = "SELECT metadata FROM free_parking WHERE identity=$id AND owner=$owner AND character=$character AND revision=$revision AND phase IN ('stored','changing','pending_sale')";
        command.Parameters.AddWithValue("$id", snapshot.Identity.ToString()); command.Parameters.AddWithValue("$owner", snapshot.Owner.ToString());
        command.Parameters.AddWithValue("$character", snapshot.Character); command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
        if (command.ExecuteScalar() is not string json) throw new InvalidOperationException("Free-parking snapshot changed.");
        var stored = JsonSerializer.Deserialize<FreeParkingSnapshot>(json)!;
        if (stored.Profile != snapshot.Profile || stored.OriginFaction != snapshot.OriginFaction) throw new InvalidOperationException("Free-parking character identity mismatch.");
        var yaml = File.ReadAllText(FreeParkingFile(stored));
        if (Hash(yaml) != stored.Checksum) throw new InvalidDataException("Free-parking snapshot checksum mismatch.");
        command.CommandText = "SELECT asset FROM free_parking_assets WHERE identity=$id AND revision=$revision";
        using var reader = command.ExecuteReader(); var assets = new List<Guid>();
        while (reader.Read()) assets.Add(Guid.Parse(reader.GetString(0)));
        return new FreeParkingRead(yaml, assets.ToArray());
    });

    private string FreeParkingFile(FreeParkingSnapshot snapshot) => Path.Combine(_freeParkingDirectory,
        snapshot.Identity.ToString("N") + "." + snapshot.Revision.ToString("N") + ".yml");

    private bool ContainsFreeParkingIdentity(Guid identity)
    {
        using var command = Command("""
            SELECT 1 FROM free_parking_assets a JOIN free_parking p ON p.identity=a.identity AND p.revision=a.revision
            WHERE a.asset=$id AND p.phase IN ('stored','claiming','changing','pending_sale') LIMIT 1
            """, ("$id", identity.ToString()));
        return command.ExecuteScalar() != null;
    }
}
