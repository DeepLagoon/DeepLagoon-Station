using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Procurement;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ProcurementBudgetTests
{
    private static ProcurementBudget Model() => new(ProcurementPolicy.Candidates[0]);
    private static void Accrue(ProcurementBudget model, int minutes, int population = 30, long start = 0)
    {
        for (var i = 1; i <= minutes; i++)
            model.Accrue(1m / 60, population, start + i * 60000L);
    }

    [Test]
    public void ThirtyActivePlayersIssueThreeHundredThousandAnHourWithSeparateProtectedPools()
    {
        var model = Model();
        Accrue(model, 60);
        Assert.That(model.Data.Issued, Is.EqualTo(300000m).Within(.00001m));
        Assert.That(model.Data.Pools[ProcurementCategory.Materials].General, Is.EqualTo(84000m).Within(.00001m));
        Assert.That(model.Data.Pools[ProcurementCategory.Materials].Protected, Is.EqualTo(36000m).Within(.00001m));
        Assert.That(model.Data.Pools[ProcurementCategory.Gas].General, Is.EqualTo(21000m).Within(.00001m));
        Assert.That(ProcurementBudget.Shares.Values.Sum(), Is.EqualTo(1));
    }

    [Test]
    public void CarryIsCappedAtTwoHoursAndAnEmptyLobbyDoesNotResetHistory()
    {
        var model = Model();
        Accrue(model, 180);
        Assert.That(model.Data.Pools.Values.Sum(p => p.General + p.Protected), Is.EqualTo(600000m).Within(.0001m));
        Assert.That(model.Data.Expired, Is.EqualTo(300000m).Within(.0001m));
        model.Accrue(1m / 60, 0, 11000000);
        Assert.That(model.Data.Pools.Values.Sum(p => p.General + p.Protected), Is.EqualTo(600000m).Within(.0001m));
        Assert.That(model.Data.Issued - model.Data.Expired - model.Data.Spent,
            Is.EqualTo(model.Data.Pools.Values.Sum(p => p.General + p.Protected)).Within(.0001m));
    }

    [Test]
    public void LargeFarmCannotSpendProtectedFundsAndRejectedLotHasNoPartialDebit()
    {
        var model = Model();
        Accrue(model, 60);
        var before = model.Data.Pools[ProcurementCategory.Materials].General;
        var decision = model.Observe(3600000, Guid.NewGuid(), ProcurementCategory.Materials, 100000);
        Assert.That(decision.Accepted, Is.False);
        Assert.That(model.Data.Pools[ProcurementCategory.Materials].General, Is.EqualTo(before));
        Assert.That(model.Data.Pools[ProcurementCategory.Materials].Protected, Is.GreaterThan(35000));
        Assert.That(model.Data.Payments, Is.Empty);
    }

    [Test]
    public void SmallQuotaIsSharedByAccountAcrossSlotsAndRollsOffAfterTwentyFourHours()
    {
        var model = Model();
        var account = Guid.NewGuid();
        Accrue(model, 120);
        for (var i = 0; i < 4; i++)
            Assert.That(model.Observe(7200000 + i, account, ProcurementCategory.Materials, 5000).ProtectedUsed, Is.EqualTo(5000));
        var sameAccountOtherSlot = model.Observe(7200010, account, ProcurementCategory.Materials, 5000);
        Assert.That(sameAccountOtherSlot.ProtectedUsed, Is.Zero);
        Assert.That(sameAccountOtherSlot.GeneralUsed, Is.EqualTo(5000));
        Assert.That(model.Observe(7200011, Guid.NewGuid(), ProcurementCategory.Materials, 5000).ProtectedUsed, Is.EqualTo(5000));
        Assert.That(model.Observe(7200004 + 86400000L, account, ProcurementCategory.Materials, 5000).ProtectedUsed, Is.EqualTo(5000));
    }

    [Test]
    public void PrincipalAndOtherCurrenciesAreNotCountedAsNewSpessosButFactionAccrualsAre()
    {
        var sale = Sale(Guid.NewGuid(), 0, 400075, 400000) with { FactionAccruals = new() { ["Medical"] = 25, ["Nfsd"] = -50 } };
        Assert.That(ProcurementBudget.Cost(sale).Cost, Is.EqualTo(100));
        Assert.That(ProcurementBudget.Cost(sale with { Currency = "MMCIndustrialCredit" }).Cost, Is.EqualTo(25));
    }

    [Test]
    public void MixedAndIncompleteManifestsAreMarkedUnmodelledRatherThanAssignedAnInventedQuota()
    {
        var model = Model();
        Accrue(model, 60);
        var incomplete = model.Observe(3600000, Guid.NewGuid(), ProcurementCategory.Materials, 5000, false);
        Assert.That(incomplete.Reason, Is.EqualTo("incomplete_manifest"));
        var mixed = model.Observe(3600000, Guid.NewGuid(), ProcurementCategory.Unclassified, 5000);
        Assert.That(mixed.Reason, Is.EqualTo("mixed_or_unclassified"));
        Assert.That(model.Data.Spent, Is.Zero);
    }

    [Test]
    public async Task JournalPersistsLimitsDeduplicatesSalesAndDoesNotAccrueOfflineTime()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "procurement-" + Guid.NewGuid() + ".db");
        var sale = Sale(Guid.NewGuid(), 3600000, 5000, 0);
        try
        {
            await using (var journal = new ProcurementJournal(path, 0))
            {
                Populate(journal);
                Assert.That(journal.TryRecord(new(sale.Id, 3600000, 213, "pallet_sale", Sale: sale)), Is.True);
                Assert.That(journal.TryRecord(new(sale.Id, 3600000, 213, "pallet_sale", Sale: sale)), Is.True);
                await journal.FlushAsync();
                Assert.That(journal.Fault, Is.Null);
            }
            var before = State(path);
            await using (var journal = new ProcurementJournal(path, 3600000 + 86400000))
            {
                Assert.That(journal.TryRecord(new(sale.Id, 3600000 + 86400000, 214, "pallet_sale", Sale: sale)), Is.True);
                await journal.FlushAsync();
            }
            var after = State(path);
            Assert.That(after.Policies.Keys, Is.EquivalentTo(before.Policies.Keys));
            foreach (var id in after.Policies.Keys)
            {
                Assert.That(after.Policies[id].Issued, Is.EqualTo(before.Policies[id].Issued));
                Assert.That(after.Policies[id].Spent, Is.EqualTo(before.Policies[id].Spent));
                Assert.That(after.Policies[id].Accepted, Is.EqualTo(1));
            }
            Assert.That(Rows(path, "pallet_sale").Count, Is.EqualTo(1));
        }
        finally { Delete(path); }
    }

    [Test]
    public async Task LinkedCashRewardUsesTheSameSaleBudgetAndMicIsKeptSeparate()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "procurement-" + Guid.NewGuid() + ".db");
        try
        {
            await using (var journal = new ProcurementJournal(path, 0))
            {
                Populate(journal);
                var sale = Sale(Guid.NewGuid(), 3600000, 3000, 0);
                journal.TryRecord(new(Guid.NewGuid(), 3600000, 213, "bounty_reward", Amount: 5000, Currency: "Credit", Parent: sale.Id));
                journal.TryRecord(new(Guid.NewGuid(), 3600000, 213, "bounty_reward", Amount: 800, Currency: "MMCIndustrialCredit", Parent: sale.Id));
                journal.TryRecord(new(sale.Id, 3600000, 213, "pallet_sale", Sale: sale));
                await journal.FlushAsync();
            }
            using var db = new SqliteConnection("Data Source=" + path);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT decisions FROM observations WHERE kind='pallet_sale'";
            var decisions = JsonSerializer.Deserialize<ProcurementShadowDecision[]>((string) cmd.ExecuteScalar()!);
            Assert.That(decisions, Has.Length.EqualTo(4));
            Assert.That(decisions!.All(d => d.Accepted && d.Cost == 8000), Is.True);
        }
        finally { Delete(path); }
    }

    [Test]
    public async Task ParkingPaymentAndManifestHaveIndependentStableIdsAndRetriesPayOnlyOnceInModel()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "procurement-" + Guid.NewGuid() + ".db");
        var operation = Guid.NewGuid();
        var player = Guid.NewGuid();
        try
        {
            await using (var journal = new ProcurementJournal(path, 0))
            {
                Populate(journal);
                var manifest = new ProcurementParkingManifest(operation, player, 2, 5000, 0, 5000, 0, new() { ["Steel"] = 10000 }, 2);
                journal.TryRecord(new(operation, 3600000, 213, "parking_manifest", Parking: manifest));
                journal.TryRecord(new(operation, 3600000, 213, "parking_payment", Player: player, Amount: 5000, Slot: 2));
                journal.TryRecord(new(operation, 3600001, 213, "parking_payment", Player: player, Amount: 5000, Slot: 2));
                await journal.FlushAsync();
            }
            Assert.That(Rows(path, "parking_manifest").Count, Is.EqualTo(1));
            Assert.That(Rows(path, "parking_payment").Count, Is.EqualTo(1));
            Assert.That(State(path).Policies.Values.All(p => p.Spent == 5000), Is.True);
        }
        finally { Delete(path); }
    }

    [Test]
    public async Task MissingPopulationSamplesDoNotProduceUnmeasuredPlayerHours()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "procurement-" + Guid.NewGuid() + ".db");
        try
        {
            await using (var journal = new ProcurementJournal(path, 0))
            {
                journal.TryRecord(new(Guid.NewGuid(), 0, 213, "population", ActivePlayers: 30));
                journal.TryRecord(new(Guid.NewGuid(), 3600000, 213, "population", ActivePlayers: 30));
                await journal.FlushAsync();
            }
            Assert.That(State(path).Policies.Values.All(p => p.Issued == 0), Is.True);
            Assert.That(Rows(path, "population").Last().Reason, Does.Contain("observation_gap"));
        }
        finally { Delete(path); }
    }

    private static ProcurementSale Sale(Guid id, long at, long cash, long principal) => new(id, at, 213,
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "Test", "Console",
        [new(1, cash, cash, ProcurementCategory.Materials)], [], cash, principal, new(), true);

    private static void Populate(ProcurementJournal journal)
    {
        for (var i = 0; i <= 60; i++)
            Assert.That(journal.TryRecord(new(Guid.NewGuid(), i * 60000L, 213, "population", ActivePlayers: 30)), Is.True);
    }
    private static ProcurementJournal.Checkpoint State(string path)
    {
        using var db = new SqliteConnection("Data Source=" + path);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM shadow_state WHERE id=1";
        return JsonSerializer.Deserialize<ProcurementJournal.Checkpoint>((string) cmd.ExecuteScalar()!)!;
    }
    private static List<ProcurementObservation> Rows(string path, string kind)
    {
        using var db = new SqliteConnection("Data Source=" + path);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM observations WHERE kind=$kind ORDER BY utc_ms,rowid";
        cmd.Parameters.AddWithValue("$kind", kind);
        using var reader = cmd.ExecuteReader();
        var rows = new List<ProcurementObservation>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<ProcurementObservation>(reader.GetString(0))!);
        return rows;
    }
    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }
}
