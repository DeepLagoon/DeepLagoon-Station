using System.IO;
using System.Text.Json;
using Content.Shared._DeepLagoon.Apartments;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.Apartments;

public sealed record ApartmentItemOrder(Guid Id, string Product, long Price, bool Delivered);

/// <summary>Storage seam: an EF/account implementation can replace the isolated prototype store.</summary>
public interface IApartmentLayoutStore : IDisposable
{
    ApartmentDelta? Load(Guid owner);
    bool Save(Guid owner, ApartmentDelta delta, int expectedRevision);
    bool Owned(Guid owner, string template);
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
}

/// <summary>Separate prototype DB. Never touches donation balances or the production schema.</summary>
public sealed class ApartmentLayoutStore : IApartmentLayoutStore
{
    private readonly SqliteConnection _connection;
    public ApartmentLayoutStore(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS layouts (owner TEXT PRIMARY KEY, revision INTEGER NOT NULL, delta TEXT NOT NULL); CREATE TABLE IF NOT EXISTS template_layouts(owner TEXT NOT NULL, template TEXT NOT NULL, delta TEXT NOT NULL, PRIMARY KEY(owner,template)); CREATE TABLE IF NOT EXISTS template_purchases(owner TEXT NOT NULL, template TEXT NOT NULL, operation TEXT NOT NULL, price INTEGER NOT NULL, granted INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(owner,template))";
        command.ExecuteNonQuery();
        Execute("CREATE TABLE IF NOT EXISTS apartment_snapshots(owner TEXT NOT NULL, template TEXT NOT NULL, data TEXT NOT NULL, round_end_data TEXT NOT NULL, PRIMARY KEY(owner,template))");
        Execute("CREATE TABLE IF NOT EXISTS item_orders(owner TEXT NOT NULL, id TEXT NOT NULL, product TEXT NOT NULL, price INTEGER NOT NULL, delivered INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(owner,id))");
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS item_orders_pending ON item_orders(owner) WHERE delivered=0");
    }

    private static readonly JsonSerializerOptions SnapshotJson = new() { IncludeFields = true };

    public ApartmentSnapshot? LoadSnapshot(Guid owner, string template)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT data FROM apartment_snapshots WHERE owner=$owner AND template=$template";
        command.Parameters.AddWithValue("$owner", owner.ToString());
        command.Parameters.AddWithValue("$template", template);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ApartmentSnapshot>(json, SnapshotJson) : null;
    }

    public void SaveSnapshot(Guid owner, string template, ApartmentSnapshot snapshot, ApartmentSnapshot roundEnd, Guid delivery = default)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            Execute("INSERT INTO apartment_snapshots VALUES($owner,$template,$data,$end) ON CONFLICT(owner,template) DO UPDATE SET data=$data,round_end_data=$end",
                ("$owner", owner.ToString()), ("$template", template),
                ("$data", JsonSerializer.Serialize(snapshot, SnapshotJson)), ("$end", JsonSerializer.Serialize(roundEnd, SnapshotJson)));
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

    private ApartmentItemOrder? FindItemOrder(Guid owner, Guid? id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id,product,price,delivered FROM item_orders WHERE owner=$owner AND " + (id == null ? "delivered=0" : "id=$id");
        command.Parameters.AddWithValue("$owner", owner.ToString());
        if (id != null) command.Parameters.AddWithValue("$id", id.Value.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3) == 1) : null;
    }

    public ApartmentItemOrder BeginItemOrder(Guid owner, Guid id, string product, long price)
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
    public void CancelItemOrder(Guid owner, Guid id) => Execute("DELETE FROM item_orders WHERE owner=$owner AND id=$id AND delivered=0",
        ("$owner", owner.ToString()), ("$id", id.ToString()));

    public void FinishRound() => Execute("UPDATE apartment_snapshots SET data=round_end_data WHERE data<>round_end_data");

    public ApartmentDelta? Load(Guid owner)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT delta FROM layouts WHERE owner=$owner";
        command.Parameters.AddWithValue("$owner", owner.ToString());
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ApartmentDelta>(json) : null;
    }

    public bool Save(Guid owner, ApartmentDelta delta, int expectedRevision)
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
    public bool Owned(Guid owner, string template)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT granted FROM template_purchases WHERE owner=$owner AND template=$template";
        command.Parameters.AddWithValue("$owner", owner.ToString()); command.Parameters.AddWithValue("$template", template);
        return command.ExecuteScalar() is long value && value == 1;
    }
    public Dictionary<string, int> Popularity()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT template,COUNT(*) FROM template_purchases WHERE granted=1 GROUP BY template";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, int>();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }
    public (string Operation, long Price) BeginPurchase(Guid owner, string template, long price)
    {
        Execute("INSERT OR IGNORE INTO template_purchases(owner,template,operation,price) VALUES($owner,$template,$operation,$price)",
            ("$owner", owner.ToString()), ("$template", template), ("$operation", Guid.NewGuid().ToString("N")), ("$price", price));
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT operation,price FROM template_purchases WHERE owner=$owner AND template=$template";
        command.Parameters.AddWithValue("$owner", owner.ToString()); command.Parameters.AddWithValue("$template", template);
        using var reader = command.ExecuteReader(); reader.Read(); return (reader.GetString(0), reader.GetInt64(1));
    }
    public void Grant(Guid owner, string template) => Execute("UPDATE template_purchases SET granted=1 WHERE owner=$owner AND template=$template",
        ("$owner", owner.ToString()), ("$template", template));
    public ApartmentDelta Select(Guid owner, ApartmentTemplatePrototype template)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            var current = Load(owner);
            if (current?.Template == template.ID) { Execute("COMMIT"); return current; }
            if (current != null) Archive(owner, current);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT delta FROM template_layouts WHERE owner=$owner AND template=$template";
            command.Parameters.AddWithValue("$owner", owner.ToString()); command.Parameters.AddWithValue("$template", template.ID);
            var selected = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ApartmentDelta>(json)!
                : ApartmentLayout.Diff(template, template.Furniture, 0);
            if (selected.TemplateVersion != template.Version) throw new InvalidOperationException("Template version requires migration.");
            selected.Revision = (current?.Revision ?? 0) + 1;
            if (!Save(owner, selected, current?.Revision ?? 0)) throw new InvalidOperationException("Layout selection conflict.");
            Execute("COMMIT"); return selected;
        }
        catch { Execute("ROLLBACK"); throw; }
    }
    public void Dispose() => _connection.Dispose();
}
