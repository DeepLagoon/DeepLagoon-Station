using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Content.Server._DeepLagoon.Persistence;
using Content.Shared._DeepLagoon.Apartments;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.Apartments;

public sealed record ApartmentItemOrder(Guid Id, string Product, long Price, bool Delivered);

/// <summary>Storage seam: an EF/account implementation can replace the isolated prototype store.</summary>
public interface IApartmentLayoutStore : IDisposable
{
    ApartmentDelta? Load(Guid owner);
    bool Save(Guid owner, ApartmentDelta delta, int expectedRevision);
    Task<bool> SaveAsync(Guid owner, ApartmentDelta delta, int expectedRevision) => Task.FromResult(Save(owner, delta, expectedRevision));
    Task SaveSnapshotAsync(Guid owner, string template, ApartmentSnapshot snapshot, ApartmentSnapshot roundEnd, Guid delivery = default)
    { SaveSnapshot(owner, template, snapshot, roundEnd, delivery); return Task.CompletedTask; }
    async Task<ApartmentSnapshotWriteResult> SaveCapturedSnapshotAsync(Guid owner, string template, ApartmentSnapshotCapture capture, ApartmentSnapshot metadata, Guid delivery = default)
    {
        // Preserve implementations of the original string-based storage seam. Freeze mutable
        // editor DTOs before the first await; only the detached grid is formatted on a worker.
        var owned = ApartmentLayoutStore.CopySnapshot(metadata);
        var formatted = await Task.Run(capture.Format);
        var (current, clean) = ApartmentSnapshotCapture.Attach(formatted, owned);
        var storageStarted = Stopwatch.GetTimestamp();
        await SaveSnapshotAsync(owner, template, current, clean, delivery);
        return new(formatted.Current.Length, formatted.Started, formatted.Yaml, formatted.RoundEndBuild,
            formatted.RoundEndYaml, Stopwatch.GetElapsedTime(storageStarted));
    }
    Task<(string Operation, long Price)> BeginPurchaseAsync(Guid owner, string template, long price) => Task.FromResult(BeginPurchase(owner, template, price));
    Task GrantAsync(Guid owner, string template) { Grant(owner, template); return Task.CompletedTask; }
    Task<ApartmentDelta> SelectAsync(Guid owner, ApartmentTemplatePrototype template) => Task.FromResult(Select(owner, template));
    Task<ApartmentItemOrder> BeginItemOrderAsync(Guid owner, Guid id, string product, long price) => Task.FromResult(BeginItemOrder(owner, id, product, price));
    Task CancelItemOrderAsync(Guid owner, Guid id) { CancelItemOrder(owner, id); return Task.CompletedTask; }
    Task FinishRoundAsync() { FinishRound(); return Task.CompletedTask; }
    Task FlushAsync() => Task.CompletedTask;
    bool Owned(Guid owner, string template);
    bool HasLayout(Guid owner, string template);
    Dictionary<string, int> Popularity();
    (string Operation, long Price) BeginPurchase(Guid owner, string template, long price);
    void Grant(Guid owner, string template);
    ApartmentDelta Select(Guid owner, ApartmentTemplatePrototype template);
    ApartmentSnapshot? LoadSnapshot(Guid owner, string template);
    void SaveSnapshot(Guid owner, string template, ApartmentSnapshot snapshot, ApartmentSnapshot roundEnd, Guid delivery = default);
    ApartmentItemOrder BeginItemOrder(Guid owner, Guid id, string product, long price);
    ApartmentItemOrder? PendingItemOrder(Guid owner);
    void CancelItemOrder(Guid owner, Guid id);
    void FinishRound();
    bool ContainsRecoveryIdentity(Guid identity) => false;
}

/// <summary>Separate prototype DB. Never touches donation balances or the production schema.</summary>
public sealed class ApartmentLayoutStore : IApartmentLayoutStore
{
    private readonly SqliteConnection _connection;
    private readonly SerialPersistenceQueue _writer = new();
    public int PendingWrites => _writer.PendingCount;
    private readonly ThreadLocal<SqliteConnection> _readers;
    private readonly bool _memory;
    private SqliteConnection ReadConnection => _writer.IsExecuting ? _connection : _readers.Value!;
    // Shared in-memory SQLite has table locks instead of WAL. Serialize its test-only readers as well.
    private T Read<T>(Func<T> read) => _memory && !_writer.IsExecuting ? _writer.Enqueue(read).GetAwaiter().GetResult() : read();
    public ApartmentLayoutStore(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        _memory = path == ":memory:";
        if (path == ":memory:") { builder.DataSource = "apartments-" + Guid.NewGuid().ToString("N"); builder.Mode = SqliteOpenMode.Memory; builder.Cache = SqliteCacheMode.Shared; }
        var connectionString = builder.ToString();
        var readString = _memory ? connectionString : new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString();
        _readers = new ThreadLocal<SqliteConnection>(() => { var reader = new SqliteConnection(readString); reader.Open(); return reader; }, true);
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS layouts (owner TEXT PRIMARY KEY, revision INTEGER NOT NULL, delta TEXT NOT NULL); CREATE TABLE IF NOT EXISTS template_layouts(owner TEXT NOT NULL, template TEXT NOT NULL, delta TEXT NOT NULL, PRIMARY KEY(owner,template)); CREATE TABLE IF NOT EXISTS template_purchases(owner TEXT NOT NULL, template TEXT NOT NULL, operation TEXT NOT NULL, price INTEGER NOT NULL, granted INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(owner,template))";
        command.ExecuteNonQuery();
        Execute("CREATE TABLE IF NOT EXISTS apartment_snapshots(owner TEXT NOT NULL, template TEXT NOT NULL, data TEXT NOT NULL, round_end_data TEXT NOT NULL, PRIMARY KEY(owner,template))");
        Execute("CREATE TABLE IF NOT EXISTS item_orders(owner TEXT NOT NULL, id TEXT NOT NULL, product TEXT NOT NULL, price INTEGER NOT NULL, delivered INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(owner,id))");
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS item_orders_pending ON item_orders(owner) WHERE delivered=0");
    }

    private static readonly JsonSerializerOptions SnapshotJson = new() { IncludeFields = true };

    public bool ContainsRecoveryIdentity(Guid identity) => Read(() =>
    {
        using var command = ReadConnection.CreateCommand();
        command.CommandText = "SELECT 1 FROM apartment_snapshots WHERE instr(lower(data),$identity)>0 LIMIT 1";
        command.Parameters.AddWithValue("$identity", identity.ToString().ToLowerInvariant());
        return command.ExecuteScalar() != null;
    });

    public ApartmentSnapshot? LoadSnapshot(Guid owner, string template) => Read(() =>
    {
        using var command = ReadConnection.CreateCommand();
        command.CommandText = "SELECT data FROM apartment_snapshots WHERE owner=$owner AND template=$template";
        command.Parameters.AddWithValue("$owner", owner.ToString());
        command.Parameters.AddWithValue("$template", template);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ApartmentSnapshot>(json, SnapshotJson) : null;
    });

    private void SaveSnapshotCore(Guid owner, string template, ApartmentSnapshot snapshot, ApartmentSnapshot roundEnd, Guid delivery = default)
    {
        var data = JsonSerializer.Serialize(snapshot, SnapshotJson);
        var endData = ReferenceEquals(snapshot, roundEnd) ? data : JsonSerializer.Serialize(roundEnd, SnapshotJson);
        Execute("BEGIN IMMEDIATE");
        try
        {
            Execute("INSERT INTO apartment_snapshots VALUES($owner,$template,$data,$end) ON CONFLICT(owner,template) DO UPDATE SET data=$data,round_end_data=$end",
                ("$owner", owner.ToString()), ("$template", template),
                ("$data", data), ("$end", endData));
            if (delivery != Guid.Empty)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE item_orders SET delivered=1 WHERE owner=$owner AND id=$id AND delivered=0";
                command.Parameters.AddWithValue("$owner", owner.ToString());
                command.Parameters.AddWithValue("$id", delivery.ToString());
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Delivery is not pending.");
            }
            Execute("COMMIT");
        }
        catch { Execute("ROLLBACK"); throw; }
    }

    public ApartmentItemOrder? PendingItemOrder(Guid owner) => FindItemOrder(owner, null);

    private ApartmentItemOrder? FindItemOrder(Guid owner, Guid? id) => Read<ApartmentItemOrder?>(() =>
    {
        using var command = ReadConnection.CreateCommand();
        command.CommandText = "SELECT id,product,price,delivered FROM item_orders WHERE owner=$owner AND " + (id == null ? "delivered=0" : "id=$id");
        command.Parameters.AddWithValue("$owner", owner.ToString());
        if (id != null) command.Parameters.AddWithValue("$id", id.Value.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3) == 1) : null;
    });

    private ApartmentItemOrder BeginItemOrderCore(Guid owner, Guid id, string product, long price)
    {
        if (id == Guid.Empty || string.IsNullOrEmpty(product) || price < 0) throw new ArgumentException("Invalid item order.");
        if (FindItemOrder(owner, id) is { } existing)
        {
            if (existing.Product != product || existing.Price != price) throw new InvalidOperationException("Параметры повторной покупки отличаются от исходных.");
            return existing;
        }
        if (PendingItemOrder(owner) != null) throw new InvalidOperationException("Сначала завершите предыдущую доставку.");
        Execute("INSERT INTO item_orders(owner,id,product,price) VALUES($owner,$id,$product,$price)",
            ("$owner", owner.ToString()), ("$id", id.ToString()), ("$product", product), ("$price", price));
        return new(id, product, price, false);
    }

    // Only call after a definite insufficient-funds rollback, never after a timeout.
    private void CancelItemOrderCore(Guid owner, Guid id) => Execute("DELETE FROM item_orders WHERE owner=$owner AND id=$id AND delivered=0",
        ("$owner", owner.ToString()), ("$id", id.ToString()));

    private void FinishRoundCore() => Execute("UPDATE apartment_snapshots SET data=round_end_data WHERE data<>round_end_data");

    public ApartmentDelta? Load(Guid owner) => Read(() =>
    {
        using var command = ReadConnection.CreateCommand();
        command.CommandText = "SELECT delta FROM layouts WHERE owner=$owner";
        command.Parameters.AddWithValue("$owner", owner.ToString());
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ApartmentDelta>(json) : null;
    });

    private bool SaveCore(Guid owner, ApartmentDelta delta, int expectedRevision)
    {
        Execute("BEGIN IMMEDIATE");
        try { var saved = SaveRows(owner, delta, expectedRevision); Execute("COMMIT"); return saved; }
        catch { Execute("ROLLBACK"); throw; }
    }

    private bool SaveRows(Guid owner, ApartmentDelta delta, int expectedRevision)
    {
        if (delta.Revision != expectedRevision + 1) throw new ArgumentException("Expected the next layout revision.");
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO layouts(owner,revision,delta) SELECT $owner,$revision,$delta WHERE $expected=0
            ON CONFLICT(owner) DO UPDATE SET revision=$revision,delta=$delta WHERE layouts.revision=$expected;
            """;
        // UPDATE separately is required for existing rows with a non-zero expected revision.
        if (expectedRevision != 0)
            command.CommandText = "UPDATE layouts SET revision=$revision,delta=$delta WHERE owner=$owner AND revision=$expected";
        command.Parameters.AddWithValue("$owner", owner.ToString());
        command.Parameters.AddWithValue("$revision", delta.Revision);
        command.Parameters.AddWithValue("$expected", expectedRevision);
        command.Parameters.AddWithValue("$delta", JsonSerializer.Serialize(delta));
        if (command.ExecuteNonQuery() != 1) return false;
        Archive(owner, delta);
        return true;
    }
    private void Execute(string sql, params (string Key, object Value)[] values)
    {
        using var command = _connection.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        command.ExecuteNonQuery();
    }
    private void Archive(Guid owner, ApartmentDelta delta) => Execute(
        "INSERT INTO template_layouts VALUES($owner,$template,$delta) ON CONFLICT(owner,template) DO UPDATE SET delta=$delta",
        ("$owner", owner.ToString()), ("$template", delta.Template), ("$delta", JsonSerializer.Serialize(delta)));
    public bool Owned(Guid owner, string template) => Read(() =>
    {
        using var command = ReadConnection.CreateCommand();
        command.CommandText = "SELECT granted FROM template_purchases WHERE owner=$owner AND template=$template";
        command.Parameters.AddWithValue("$owner", owner.ToString()); command.Parameters.AddWithValue("$template", template);
        return command.ExecuteScalar() is long value && value == 1;
    });
    public bool HasLayout(Guid owner, string template) => Read(() =>
    {
        using var command = ReadConnection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM template_layouts WHERE owner=$owner AND template=$template UNION ALL SELECT 1 FROM apartment_snapshots WHERE owner=$owner AND template=$template)";
        command.Parameters.AddWithValue("$owner", owner.ToString());
        command.Parameters.AddWithValue("$template", template);
        return command.ExecuteScalar() is long value && value == 1;
    });
    public Dictionary<string, int> Popularity() => Read(() =>
    {
        using var command = ReadConnection.CreateCommand();
        command.CommandText = "SELECT template,COUNT(*) FROM template_purchases WHERE granted=1 GROUP BY template";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, int>();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    });
    private (string Operation, long Price) BeginPurchaseCore(Guid owner, string template, long price)
    {
        Execute("INSERT OR IGNORE INTO template_purchases(owner,template,operation,price) VALUES($owner,$template,$operation,$price)",
            ("$owner", owner.ToString()), ("$template", template), ("$operation", Guid.NewGuid().ToString("N")), ("$price", price));
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT operation,price FROM template_purchases WHERE owner=$owner AND template=$template";
        command.Parameters.AddWithValue("$owner", owner.ToString()); command.Parameters.AddWithValue("$template", template);
        using var reader = command.ExecuteReader(); reader.Read(); return (reader.GetString(0), reader.GetInt64(1));
    }
    private void GrantCore(Guid owner, string template) => Execute("UPDATE template_purchases SET granted=1 WHERE owner=$owner AND template=$template",
        ("$owner", owner.ToString()), ("$template", template));
    private ApartmentDelta SelectCore(Guid owner, ApartmentTemplatePrototype template)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            using var currentCommand = _connection.CreateCommand();
            currentCommand.CommandText = "SELECT delta FROM layouts WHERE owner=$owner";
            currentCommand.Parameters.AddWithValue("$owner", owner.ToString());
            var current = currentCommand.ExecuteScalar() is string currentJson ? JsonSerializer.Deserialize<ApartmentDelta>(currentJson) : null;
            if (current?.Template == template.ID) { Execute("COMMIT"); return current; }
            if (current != null) Archive(owner, current);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT delta FROM template_layouts WHERE owner=$owner AND template=$template";
            command.Parameters.AddWithValue("$owner", owner.ToString()); command.Parameters.AddWithValue("$template", template.ID);
            var selected = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ApartmentDelta>(json)!
                : ApartmentLayout.Diff(template, template.Furniture, 0);
            if (selected.TemplateVersion != template.Version) throw new InvalidOperationException("Template version requires migration.");
            selected.Revision = (current?.Revision ?? 0) + 1;
            if (!SaveRows(owner, selected, current?.Revision ?? 0)) throw new InvalidOperationException("Layout selection conflict.");
            Execute("COMMIT"); return selected;
        }
        catch { Execute("ROLLBACK"); throw; }
    }
    public bool Save(Guid owner, ApartmentDelta delta, int expectedRevision) => SaveAsync(owner, delta, expectedRevision).GetAwaiter().GetResult();
    public Task<bool> SaveAsync(Guid owner, ApartmentDelta delta, int expectedRevision) { var owned = CopyDelta(delta); return _writer.Enqueue(() => SaveCore(owner, owned, expectedRevision)); }
    public void SaveSnapshot(Guid owner, string template, ApartmentSnapshot snapshot, ApartmentSnapshot roundEnd, Guid delivery = default) => SaveSnapshotAsync(owner, template, snapshot, roundEnd, delivery).GetAwaiter().GetResult();
    public Task SaveSnapshotAsync(Guid owner, string template, ApartmentSnapshot snapshot, ApartmentSnapshot roundEnd, Guid delivery = default)
    {
        var full = CopySnapshot(snapshot);
        var clean = ReferenceEquals(snapshot, roundEnd) ? full : CopySnapshot(roundEnd);
        return _writer.Enqueue(() => SaveSnapshotCore(owner, template, full, clean, delivery));
    }
    public Task<ApartmentSnapshotWriteResult> SaveCapturedSnapshotAsync(Guid owner, string template, ApartmentSnapshotCapture capture, ApartmentSnapshot metadata, Guid delivery = default)
    {
        var owned = CopySnapshot(metadata);
        return _writer.Enqueue(() =>
        {
            try
            {
                var formatted = capture.Format();
                var (current, clean) = ApartmentSnapshotCapture.Attach(formatted, owned);
                var storageStarted = Stopwatch.GetTimestamp();
                SaveSnapshotCore(owner, template, current, clean, delivery);
                return new ApartmentSnapshotWriteResult(formatted.Current.Length, formatted.Started, formatted.Yaml,
                    formatted.RoundEndBuild, formatted.RoundEndYaml, Stopwatch.GetElapsedTime(storageStarted));
            }
            catch (Exception error)
            {
                if (!error.Data.Contains("ApartmentSaveStage")) error.Data["ApartmentSaveStage"] = "storage_worker";
                throw;
            }
        });
    }
    public (string Operation, long Price) BeginPurchase(Guid owner, string template, long price) => BeginPurchaseAsync(owner, template, price).GetAwaiter().GetResult();
    public Task<(string Operation, long Price)> BeginPurchaseAsync(Guid owner, string template, long price) => _writer.Enqueue(() => BeginPurchaseCore(owner, template, price));
    public void Grant(Guid owner, string template) => GrantAsync(owner, template).GetAwaiter().GetResult();
    public Task GrantAsync(Guid owner, string template) => _writer.Enqueue(() => GrantCore(owner, template));
    public ApartmentDelta Select(Guid owner, ApartmentTemplatePrototype template) => SelectAsync(owner, template).GetAwaiter().GetResult();
    public Task<ApartmentDelta> SelectAsync(Guid owner, ApartmentTemplatePrototype template) => _writer.Enqueue(() => SelectCore(owner, template));
    public ApartmentItemOrder BeginItemOrder(Guid owner, Guid id, string product, long price) => BeginItemOrderAsync(owner, id, product, price).GetAwaiter().GetResult();
    public Task<ApartmentItemOrder> BeginItemOrderAsync(Guid owner, Guid id, string product, long price) => _writer.Enqueue(() => BeginItemOrderCore(owner, id, product, price));
    public void CancelItemOrder(Guid owner, Guid id) => CancelItemOrderAsync(owner, id).GetAwaiter().GetResult();
    public Task CancelItemOrderAsync(Guid owner, Guid id) => _writer.Enqueue(() => CancelItemOrderCore(owner, id));
    public void FinishRound() => FinishRoundAsync().GetAwaiter().GetResult();
    public Task FinishRoundAsync() => _writer.Enqueue(() => FinishRoundCore());
    public Task FlushAsync() => _writer.FlushAsync();
    private static ApartmentDelta CopyDelta(ApartmentDelta delta) => new()
    {
        SchemaVersion = delta.SchemaVersion, Template = delta.Template, TemplateVersion = delta.TemplateVersion,
        Revision = delta.Revision, OxygenPercent = delta.OxygenPercent, Removed = new(delta.Removed),
        Changed = delta.Changed.Select(p => p.Copy()).ToList(), Added = delta.Added.Select(p => p.Copy()).ToList(),
        PurchasedPlacements = new(delta.PurchasedPlacements),
    };
    internal static ApartmentSnapshot CopySnapshot(ApartmentSnapshot snapshot) => new()
    {
        Grid = snapshot.Grid, Layout = CopyDelta(snapshot.Layout),
        MapFurniture = snapshot.MapFurniture.Select(p => new ApartmentMapFurniture { Baseline = p.Baseline.Copy(),
            Prototype = p.Prototype, Name = p.Name, BlocksMovement = p.BlocksMovement,
            AllowsOverlap = p.AllowsOverlap, CanRotate = p.CanRotate }).ToList(),
    };
    public void Dispose()
    {
        _writer.Dispose();
        foreach (var reader in _readers.Values) reader.Dispose();
        _readers.Dispose(); _connection.Dispose();
    }
}
