using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Shared._DeepLagoon.ShuttleParking;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed record StoredShuttle(Guid Identity, string Name, string OwnerName, int Category, int OriginalPrice, string Grid, string Checksum, int Version = 1);
public sealed record ParkingSlot(Guid Id, int Category, StoredShuttle? Ship, string Phase, Guid Redemption, int Character, bool Paid);
public sealed record ParkingPurchase(Guid Id, int Category, int Cost, bool Completed);

/// <summary>Snapshots and their state transitions commit together to a durable SQLite journal.</summary>
public sealed class ParkingStore : IDisposable
{
    private readonly SqliteConnection _db;
    public ParkingStore(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS parking_slots(
              id TEXT PRIMARY KEY, owner TEXT NOT NULL, category INTEGER NOT NULL,
              ship TEXT, phase TEXT NOT NULL DEFAULT 'empty', operation TEXT NOT NULL DEFAULT '',
              character INTEGER NOT NULL DEFAULT -1, paid INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS parking_ship_identity ON parking_slots(json_extract(ship,'$.Identity')) WHERE ship IS NOT NULL;
            CREATE TABLE IF NOT EXISTS parking_purchases(
              id TEXT PRIMARY KEY, owner TEXT NOT NULL, category INTEGER NOT NULL, cost INTEGER NOT NULL, completed INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS parking_pending_purchase ON parking_purchases(owner) WHERE completed=0;
            """);
        // World entities from an interrupted process no longer exist. Finish committed saves.
        Execute("UPDATE parking_slots SET phase='stored' WHERE phase='saving'");
    }
    public static string Hash(string grid) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(grid)));
    public List<ParkingSlot> Slots(Guid owner)
    {
        using var cmd = Command("SELECT id,category,ship,phase,operation,character,paid FROM parking_slots WHERE owner=$owner ORDER BY category,rowid", ("$owner", owner.ToString()));
        using var reader = cmd.ExecuteReader();
        var result = new List<ParkingSlot>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1),
            reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<StoredShuttle>(reader.GetString(2)), reader.GetString(3),
            Guid.TryParse(reader.GetString(4), out var operation) ? operation : Guid.Empty, reader.GetInt32(5), reader.GetInt64(6) != 0));
        return result;
    }
    public ParkingSlot Slot(Guid owner, Guid id) => Slots(owner).Find(s => s.Id == id) ?? throw new InvalidOperationException("Это место вам не принадлежит.");
    public ParkingPurchase? PendingPurchase(Guid owner) => Purchase(owner, null);
    private ParkingPurchase? Purchase(Guid owner, Guid? id)
    {
        using var cmd = Command("SELECT id,category,cost,completed FROM parking_purchases WHERE owner=$owner AND " + (id == null ? "completed=0" : "id=$id"),
            ("$owner", owner.ToString()), ("$id", id?.ToString() ?? ""));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3) != 0) : null;
    }
    public ParkingPurchase BeginPurchase(Guid owner, Guid request, int category)
    {
        if (request == Guid.Empty || category is < 0 or > 4) throw new ArgumentException("Некорректная покупка.");
        Execute("BEGIN IMMEDIATE");
        try
        {
            var old = Purchase(owner, request) ?? PendingPurchase(owner);
            if (old != null)
            {
                if (old.Category != category) throw new InvalidOperationException("Сначала завершите предыдущую покупку.");
                Execute("COMMIT"); return old;
            }
            var price = ParkingRules.SlotPrice(category, Slots(owner).Exists(s => s.Category == category));
            Execute("INSERT INTO parking_purchases(id,owner,category,cost) VALUES($id,$owner,$category,$cost)",
                ("$id", request.ToString()), ("$owner", owner.ToString()), ("$category", category), ("$cost", price));
            Execute("COMMIT"); return new(request, category, price, false);
        }
        catch { Execute("ROLLBACK"); throw; }
    }
    public void GrantPurchase(Guid owner, ParkingPurchase purchase)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            var order = Purchase(owner, purchase.Id) ?? throw new InvalidOperationException("Покупка не найдена.");
            if (!order.Completed)
            {
                Execute("INSERT INTO parking_slots(id,owner,category) VALUES($id,$owner,$category)",
                    ("$id", order.Id.ToString()), ("$owner", owner.ToString()), ("$category", order.Category));
                Execute("UPDATE parking_purchases SET completed=1 WHERE id=$id", ("$id", order.Id.ToString()));
            }
            Execute("COMMIT");
        }
        catch { Execute("ROLLBACK"); throw; }
    }
    public void CancelPurchase(Guid owner, Guid id) => Execute("DELETE FROM parking_purchases WHERE id=$id AND owner=$owner AND completed=0", ("$id", id.ToString()), ("$owner", owner.ToString()));
    public void Save(Guid owner, Guid slot, StoredShuttle ship)
    {
        if (ship.Version != 1 || ship.Identity == Guid.Empty || ship.Grid.Length > 32_000_000 || ship.Checksum != Hash(ship.Grid)) throw new InvalidDataException("Некорректный снимок шаттла.");
        var target = Slot(owner, slot);
        if (target.Ship != null || target.Phase != "empty" || target.Category < ship.Category) throw new InvalidOperationException("Шаттл не помещается в свободное место.");
        ParkingRules.RedemptionPrice(ship.OriginalPrice);
        using var cmd = Command("UPDATE parking_slots SET ship=$ship,phase='saving' WHERE id=$id AND owner=$owner AND phase='empty' AND ship IS NULL",
            ("$ship", JsonSerializer.Serialize(ship)), ("$id", slot.ToString()), ("$owner", owner.ToString()));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Место уже занято.");
    }
    public void FinishSave(Guid owner, Guid slot) => Execute("UPDATE parking_slots SET phase='stored' WHERE owner=$owner AND id=$id AND phase='saving'", ("$owner", owner.ToString()), ("$id", slot.ToString()));
    public ParkingSlot BeginRedeem(Guid owner, Guid slot, int character)
    {
        var target = Slot(owner, slot);
        if (target.Ship == null || target.Phase is not ("stored" or "redeeming")) throw new InvalidOperationException("Шаттл недоступен.");
        if (target.Ship.Version != 1 || Hash(target.Ship.Grid) != target.Ship.Checksum) throw new InvalidDataException("Снимок повреждён; сохранение оставлено для восстановления.");
        if (target.Phase == "redeeming")
        {
            if (target.Character != character) throw new InvalidOperationException("Завершите выкуп тем же персонажем.");
            return target;
        }
        var operation = Guid.NewGuid();
        using var cmd = Command("UPDATE parking_slots SET phase='redeeming',operation=$operation,character=$character,paid=0 WHERE id=$id AND owner=$owner AND phase='stored'",
            ("$operation", operation.ToString()), ("$character", character), ("$id", slot.ToString()), ("$owner", owner.ToString()));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Выкуп уже обрабатывается.");
        return target with { Phase = "redeeming", Redemption = operation, Character = character, Paid = false };
    }
    public void MarkPaid(Guid owner, Guid slot) => Execute("UPDATE parking_slots SET paid=1 WHERE owner=$owner AND id=$id AND phase='redeeming'", ("$owner", owner.ToString()), ("$id", slot.ToString()));
    public void CompleteRedeem(Guid owner, Guid slot)
    {
        using var cmd = Command("UPDATE parking_slots SET ship=NULL,phase='empty',operation='',character=-1,paid=0 WHERE id=$id AND owner=$owner AND phase='redeeming' AND paid=1",
            ("$owner", owner.ToString()), ("$id", slot.ToString()));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Выкуп не оплачен.");
    }
    private SqliteCommand Command(string sql, params (string Key, object Value)[] values)
    {
        var cmd = _db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }
    private void Execute(string sql, params (string Key, object Value)[] values) { using var cmd = Command(sql, values); cmd.ExecuteNonQuery(); }
    public void Dispose() => _db.Dispose();
}
