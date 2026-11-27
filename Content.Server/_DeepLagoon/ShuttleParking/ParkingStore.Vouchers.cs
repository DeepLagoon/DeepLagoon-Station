using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed record StoredVoucherSale(Guid Owner, Guid Slot, int Character, StoredShuttle Ship);

public sealed partial class ParkingStore
{
    private readonly ConcurrentDictionary<Guid, string> _storedVessels = new();
    private readonly ConcurrentDictionary<Guid, byte> _releasedVessels = new();
    private Task<List<StoredVoucherSale>>? _originScan;
    private bool _originIndexReady;

    private void InitializeStoredOrigins()
    {
        var connectionString = _connectionString;
        _originScan = Task.Run(() =>
        {
            var sales = new List<StoredVoucherSale>();
            using var db = new SqliteConnection(connectionString);
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT owner,id,character,ship FROM parking_slots WHERE ship IS NOT NULL";
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                var ship = JsonSerializer.Deserialize<StoredShuttle>(rows.GetString(3));
                if (ship == null) continue;
                // Old snapshots already contain the authoritative ParkingShuttle.vessel field.
                // Read only the root, never a passenger's deed, price, name or prototype resemblance.
                var origin = ReadStoredOrigin(ship);
                if (origin.Vessel is { Length: > 0 } vessel && !_releasedVessels.ContainsKey(ship.Identity))
                    _storedVessels.TryAdd(ship.Identity, vessel);
                if (origin.Voucher)
                    sales.Add(new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetInt32(2),
                        ship with { PurchasedWithVoucher = true, OwnerProfile = origin.Profile,
                            OriginFaction = origin.Faction, Vessel = origin.Vessel }));
            }
            return sales;
        });
    }

    public bool TryCopyStoredVesselReservations(Dictionary<Guid, string> destination)
    {
        if (!_originIndexReady)
        {
            if (_originScan is not { IsCompletedSuccessfully: true }) return false;
            _originIndexReady = true;
        }
        foreach (var (identity, vessel) in _storedVessels)
            if (!_releasedVessels.ContainsKey(identity)) destination[identity] = vessel;
        return true;
    }

    internal Task<List<StoredVoucherSale>> StoredVoucherSales => _originScan!;

    private void ReserveStoredVessel(StoredShuttle ship)
    {
        _releasedVessels.TryRemove(ship.Identity, out _);
        if (ship.Vessel is { Length: > 0 } vessel) _storedVessels[ship.Identity] = vessel;
    }

    private void ReleaseStoredVessel(Guid identity)
    {
        _releasedVessels[identity] = 0;
        _storedVessels.TryRemove(identity, out _);
    }

    /// <summary>Durable escrow transition: the source remains recoverable until payout and removal commit together.</summary>
    public Task<bool> CommitStoredVoucherSaleAsync(StoredVoucherSale sale, ParkingPayout payout)
    {
        if (!sale.Ship.PurchasedWithVoucher || payout.Id != sale.Ship.Identity || payout.Owner != sale.Owner || payout.Amount < 0 ||
            payout.ProfileId <= 0 || payout.ProfileId != sale.Ship.OwnerProfile)
            throw new InvalidOperationException("Отсутствует подтверждённое ваучерное происхождение или квитанция продажи.");
        var connectionString = _connectionString;
        return Task.Run(() =>
        {
            using var db = new SqliteConnection(connectionString);
            db.Open();
            using (var settings = db.CreateCommand())
            {
                settings.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
                settings.ExecuteNonQuery();
            }
            using var transaction = db.BeginTransaction(deferred: false);
            using var remove = db.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = """
                UPDATE parking_slots SET ship=NULL,phase='empty',operation='',character=-1,paid=0
                WHERE owner=$owner AND id=$slot AND phase IN ('stored','saving')
                AND json_extract(ship,'$.Identity')=$identity AND json_extract(ship,'$.Checksum')=$checksum
                """;
            remove.Parameters.AddWithValue("$owner", sale.Owner.ToString());
            remove.Parameters.AddWithValue("$slot", sale.Slot.ToString());
            remove.Parameters.AddWithValue("$identity", sale.Ship.Identity.ToString());
            remove.Parameters.AddWithValue("$checksum", sale.Ship.Checksum);
            if (remove.ExecuteNonQuery() != 1) return false;
            using var receipt = db.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO parking_payouts(id,owner,character,amount,metadata)
                VALUES($id,$owner,$character,$amount,$metadata) ON CONFLICT(id) DO NOTHING
                """;
            receipt.Parameters.AddWithValue("$id", payout.Id.ToString());
            receipt.Parameters.AddWithValue("$owner", payout.Owner.ToString());
            receipt.Parameters.AddWithValue("$character", payout.Character);
            receipt.Parameters.AddWithValue("$amount", payout.Amount);
            receipt.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(payout));
            receipt.ExecuteNonQuery();
            transaction.Commit();
            ReleaseStoredVessel(sale.Ship.Identity);
            return true;
        });
    }

    public static (bool Voucher, string? Faction, int Profile, string? Vessel) ReadStoredOrigin(StoredShuttle ship)
    {
        var result = (Voucher: ship.PurchasedWithVoucher, Faction: ship.OriginFaction, Profile: ship.OwnerProfile, Vessel: ship.Vessel);
        // These fields are emitted together from the authoritative purchase provenance.
        // Legacy records have absent/default fields and must still inspect their saved root.
        if (ship.OwnerProfile > 0 && !string.IsNullOrEmpty(ship.OriginFaction) && !string.IsNullOrEmpty(ship.Vessel))
            return result;
        if (string.IsNullOrEmpty(ship.Grid) || ship.Checksum != Hash(ship.Grid)) return result;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(ship.Grid));
            if (stream.Documents.Single().RootNode is not YamlMappingNode root ||
                !root.Children.TryGetValue(new YamlScalarNode("grids"), out var grids) || grids is not YamlSequenceNode gridIds ||
                gridIds.Children.Count != 1 || !root.Children.TryGetValue(new YamlScalarNode("entities"), out var entityNode) ||
                entityNode is not YamlSequenceNode groups) return result;
            var gridUid = ((YamlScalarNode)gridIds.Children[0]).Value;
            foreach (var groupNode in groups.Children.OfType<YamlMappingNode>())
            {
                if (!groupNode.Children.TryGetValue(new YamlScalarNode("entities"), out var list) || list is not YamlSequenceNode entities) continue;
                foreach (var entity in entities.Children.OfType<YamlMappingNode>())
                {
                    if (Value(entity, "uid") != gridUid || !entity.Children.TryGetValue(new YamlScalarNode("components"), out var nodes) ||
                        nodes is not YamlSequenceNode components) continue;
                    foreach (var component in components.Children.OfType<YamlMappingNode>())
                    {
                        var type = Value(component, "type");
                        if (type is not ("ParkingShuttle" or "ShuttleDeed")) continue;
                        result.Voucher |= bool.TryParse(Value(component, "purchasedWithVoucher"), out var voucher) && voucher;
                        if (type != "ParkingShuttle") continue;
                        result.Faction ??= Value(component, "originFaction");
                        if (result.Profile == 0 && int.TryParse(Value(component, "parkingProfile"), out var profile)) result.Profile = profile;
                        result.Vessel ??= Value(component, "vessel");
                    }
                }
            }
        }
        catch (Exception) { /* Invalid legacy data is preserved; it is never guessed to be a voucher. */ }
        return result;
    }

    private static string? Value(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;
}
