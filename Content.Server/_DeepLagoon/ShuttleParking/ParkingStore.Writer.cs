using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Shared._DeepLagoon.ShuttleParking;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingStore
{
    /// <summary>The worker owns immutable data and a separate connection; no ECS or live connection access.</summary>
    public Task SaveAsync(Guid owner, Guid slot, StoredShuttle ship, ParkingPayout? payout = null)
        => WriteSave(owner, slot, ship, payout, captureChecksum: false);

    internal Task SaveCapturedAsync(Guid owner, Guid slot, StoredShuttle ship, ParkingPayout? payout = null)
        => WriteSave(owner, slot, ship, payout, captureChecksum: true);

    private Task WriteSave(Guid owner, Guid slot, StoredShuttle ship, ParkingPayout? payout, bool captureChecksum)
    {
        var connectionString = _connectionString;
        return Task.Run(() =>
        {
            var checksum = Hash(ship.Grid);
            if (ship.Version != 1 || ship.Identity == Guid.Empty || ship.Grid.Length > 32_000_000
                || !captureChecksum && ship.Checksum != checksum) throw new InvalidDataException("Некорректный снимок шаттла.");
            if (captureChecksum) ship = ship with { Checksum = checksum };
            ParkingRules.RedemptionPrice(ship.OriginalPrice);
            var json = JsonSerializer.Serialize(ship);
            using var db = new SqliteConnection(connectionString);
            db.Open();
            using (var settings = db.CreateCommand())
            {
                settings.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
                settings.ExecuteNonQuery();
            }
            using var transaction = db.BeginTransaction(deferred: false);
            using var save = db.CreateCommand();
            save.Transaction = transaction;
            save.CommandText = """
                UPDATE parking_slots SET ship=$ship,phase='saving'
                WHERE id=$id AND owner=$owner AND phase='empty' AND ship IS NULL AND category >= $category
                """;
            save.Parameters.AddWithValue("$ship", json);
            save.Parameters.AddWithValue("$id", slot.ToString());
            save.Parameters.AddWithValue("$owner", owner.ToString());
            save.Parameters.AddWithValue("$category", ship.Category);
            if (save.ExecuteNonQuery() != 1) throw new InvalidOperationException("Место уже занято или не подходит шаттлу.");
            if (payout != null)
            {
                using var receipt = db.CreateCommand();
                receipt.Transaction = transaction;
                receipt.CommandText = "INSERT INTO parking_payouts(id,owner,character,amount,metadata) VALUES($id,$owner,$character,$amount,$metadata) ON CONFLICT(id) DO NOTHING";
                receipt.Parameters.AddWithValue("$id", payout.Id.ToString());
                receipt.Parameters.AddWithValue("$owner", payout.Owner.ToString());
                receipt.Parameters.AddWithValue("$character", payout.Character);
                receipt.Parameters.AddWithValue("$amount", payout.Amount);
                receipt.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(payout));
                receipt.ExecuteNonQuery();
            }
            // Paid storage supersedes the crash copy in the same transaction. A crash cannot expose both.
            using (var free = db.CreateCommand())
            {
                free.Transaction = transaction;
                free.CommandText = "DELETE FROM free_parking_assets WHERE identity=$identity; DELETE FROM free_parking_pending WHERE identity=$identity; DELETE FROM free_parking WHERE identity=$identity";
                free.Parameters.AddWithValue("$identity", ship.Identity.ToString()); free.ExecuteNonQuery();
            }
            transaction.Commit();
            ReserveStoredVessel(ship);
        });
    }

    public Task FinishSaveAsync(Guid owner, Guid slot, TimeSpan freeReturnAt)
    {
        var connectionString = _connectionString;
        return Task.Run(() =>
        {
            using var db = new SqliteConnection(connectionString);
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            command.CommandText = """
                UPDATE parking_slots SET phase='stored',ship=json_set(ship,'$.FreeReturnAt',$deadline)
                WHERE owner=$owner AND id=$id AND phase='saving'
                """;
            command.Parameters.AddWithValue("$owner", owner.ToString());
            command.Parameters.AddWithValue("$id", slot.ToString());
            command.Parameters.AddWithValue("$deadline", freeReturnAt.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        });
    }
}
