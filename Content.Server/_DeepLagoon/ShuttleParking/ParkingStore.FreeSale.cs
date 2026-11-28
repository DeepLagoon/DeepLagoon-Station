using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingStore
{
    public Task<FreeParkingSnapshot[]> PendingFreeParkingSalesAsync() => Task.Run(() =>
    {
        using var db = FreeParkingConnection(); using var command = db.CreateCommand();
        command.CommandText = "SELECT metadata FROM free_parking WHERE phase='pending_sale'";
        using var reader = command.ExecuteReader(); var snapshots = new List<FreeParkingSnapshot>();
        while (reader.Read()) snapshots.Add(JsonSerializer.Deserialize<FreeParkingSnapshot>(reader.GetString(0))!);
        return snapshots.ToArray();
    });

    public Task MarkFreeParkingSalesAsync(FreeParkingSnapshot[] snapshots) => FreeParkingWrite(db =>
    {
        using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE free_parking SET phase='pending_sale' WHERE identity=$id AND revision=$revision AND owner=$owner AND character=$character AND phase IN ('stored','changing')";
        command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$revision", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$owner", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$character", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var snapshot in snapshots)
        {
            command.Parameters["$id"].Value = snapshot.Identity.ToString(); command.Parameters["$revision"].Value = snapshot.Revision.ToString();
            command.Parameters["$owner"].Value = snapshot.Owner.ToString(); command.Parameters["$character"].Value = snapshot.Character;
            command.ExecuteNonQuery();
        }
        transaction.Commit(); return true;
    });

    public Task CommitFreeParkingSaleAsync(FreeParkingSnapshot snapshot, ParkingPayout payout) => FreeParkingWrite(db =>
    {
        if (payout.Id != snapshot.Identity || payout.Owner != snapshot.Owner || payout.Character != snapshot.Character ||
            payout.ProfileId != snapshot.Profile || payout.Amount < 0) throw new InvalidOperationException("Invalid recovered parking sale receipt.");
        using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", snapshot.Identity.ToString()); command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString());
        command.CommandText = "SELECT metadata FROM free_parking WHERE identity=$id AND revision=$revision AND phase='pending_sale'";
        if (command.ExecuteScalar() is not string json) throw new InvalidOperationException("Recovered parking sale changed.");
        var stored = JsonSerializer.Deserialize<FreeParkingSnapshot>(json)!;
        if (stored.Owner != snapshot.Owner || stored.Profile != snapshot.Profile) throw new InvalidOperationException("Recovered parking sale owner changed.");
        command.CommandText = "INSERT INTO parking_payouts(id,owner,character,amount,metadata) VALUES($id,$owner,$character,$amount,$metadata) ON CONFLICT(id) DO NOTHING";
        command.Parameters.AddWithValue("$owner", payout.Owner.ToString()); command.Parameters.AddWithValue("$character", payout.Character);
        command.Parameters.AddWithValue("$amount", payout.Amount); command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(payout)); command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM free_parking_assets WHERE identity=$id AND revision=$revision; DELETE FROM free_parking_pending WHERE identity=$id; DELETE FROM free_parking WHERE identity=$id AND revision=$revision";
        command.ExecuteNonQuery(); transaction.Commit();
        if (_freeParkingDirectory.Length > 0) File.Delete(FreeParkingFile(stored));
        return true;
    });
}
