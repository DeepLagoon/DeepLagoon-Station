using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._NF.Bank;
using Content.Server.Mind;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingOriginTests
{
    [SetUp]
    public void InitializeSqliteProvider()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
    }

    [Test]
    public async Task AutomaticParkingRequiresProvenOriginalOwnerCharacterAndFaction()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var parking = entities.System<ParkingSystem>();
            entities.EnsureComponent<ShipOwnershipComponent>(map.Grid).OwnerUserId = new NetUserId(Guid.NewGuid());
            entities.EnsureComponent<VesselComponent>(map.Grid).VesselId = pair.Server.ResolveDependency<IPrototypeManager>()
                .EnumeratePrototypes<VesselPrototype>().First().ID;
            var autoOwner = typeof(ParkingSystem).GetMethod("GetAutoParkingOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.That(autoOwner.Invoke(parking, [map.Grid.Owner]), Is.Null);
            Assert.That(entities.HasComponent<ParkingShuttleComponent>(map.Grid), Is.False,
                "A legacy ownership record is insufficient to manufacture the missing character/faction provenance.");
            var owner = Guid.NewGuid();
            parking.RegisterPurchasedShip(map.Grid, owner, 100, "same-vessel", "Captain");
            var ship = entities.GetComponent<ParkingShuttleComponent>(map.Grid);
            var cases = new (int Profile, string Faction, bool KnownOwner, bool Eligible)[]
            {
                (0, "TSF", true, false),
                (42, null, true, false),
                (42, "", true, false),
                (42, "TSF", false, false),
                (42, "TSF", true, true),
            };
            Assert.Multiple(() =>
            {
                foreach (var test in cases)
                {
                    ship.OwnerAccount = test.KnownOwner ? owner : Guid.Empty;
                    ship.ParkingProfile = test.Profile;
                    ship.OriginFaction = test.Faction;
                    Assert.That(autoOwner.Invoke(parking, [map.Grid.Owner]) != null, Is.EqualTo(test.Eligible),
                        $"Unknown provenance must remain in the world: profile={test.Profile}, faction={test.Faction ?? "null"}, owner={test.KnownOwner}.");
                    Assert.That(ship.ParkingProfile, Is.EqualTo(test.Profile));
                    Assert.That(ship.OriginFaction, Is.EqualTo(test.Faction), "Automatic eligibility never adopts a faction from a card or current job.");
                }
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CharacterAndServerJobRemainRequiredAcrossBodyAndFactionChanges()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var parking = entities.System<ParkingSystem>();
            var actor = entities.SpawnEntity("MobHuman", map.GridCoords);
            var identity = entities.EnsureComponent<BankCharacterComponent>(actor);
            identity.User = Guid.NewGuid(); identity.Profile = 42;
            var mind = entities.System<SharedMindSystem>().CreateMind(null);
            entities.System<SharedMindSystem>().TransferTo(mind, actor);
            var roles = entities.System<SharedRoleSystem>();
            roles.MindAddJobRole(mind, silent: true, jobPrototype: "Contractor");
            var faction = parking.GetParkingOriginFaction(actor);
            Assert.That(faction, Is.Not.Null);
            var source = new ParkingShuttleComponent { OwnerAccount = identity.User, ParkingProfile = 42, OriginFaction = faction };
            Assert.DoesNotThrow(() => parking.ValidateParkingOrigin(actor, source));
            identity.Profile = 43;
            Assert.Throws<InvalidOperationException>(() => parking.ValidateParkingOrigin(actor, source));
            identity.Profile = 42;
            roles.MindRemoveRole<JobRoleComponent>(mind);
            roles.MindAddJobRole(mind, silent: true, jobPrototype: "Brigmedic");
            Assert.That(parking.GetParkingOriginFaction(actor), Is.Not.EqualTo(faction));
            Assert.Throws<InvalidOperationException>(() => parking.ValidateParkingOrigin(actor, source));
            roles.MindRemoveRole<JobRoleComponent>(mind);
            roles.MindAddJobRole(mind, silent: true, jobPrototype: "Contractor");
            Assert.DoesNotThrow(() => parking.ValidateParkingOrigin(actor, source));
            var replacement = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.System<SharedMindSystem>().TransferTo(mind, replacement);
            var restoredIdentity = entities.EnsureComponent<BankCharacterComponent>(replacement);
            restoredIdentity.User = identity.User; restoredIdentity.Profile = identity.Profile;
            Assert.DoesNotThrow(() => parking.ValidateParkingOrigin(replacement, source),
                "A replacement body with the same immutable profile and mind job retains access.");
            source.OriginFaction = null;
            Assert.Throws<InvalidOperationException>(() => parking.ValidateParkingOrigin(replacement, source));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VoucherProvenanceSurvivesDeedRemovalAndBlocksEveryParkingCapture()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var parking = entities.System<ParkingSystem>();
            var voucherDeed = entities.EnsureComponent<ShuttleDeedComponent>(map.Grid);
            typeof(ShuttleDeedComponent).GetField(nameof(ShuttleDeedComponent.PurchasedWithVoucher))!.SetValue(voucherDeed, true);
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 100, "same-vessel", "Captain");
            entities.RemoveComponent<ShuttleDeedComponent>(map.Grid);
            Assert.That(parking.IsVoucherPurchasedShuttle(map.Grid), Is.True);
            Assert.Throws<InvalidOperationException>(() => parking.SerializeSnapshot(map.Grid));
            var autoOwner = typeof(ParkingSystem).GetMethod("GetAutoParkingOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.That(autoOwner.Invoke(parking, [map.Grid.Owner]), Is.Null);
            var park = typeof(ParkingSystem).GetMethod("ParkShuttle", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.That(Assert.Throws<TargetInvocationException>(() => park.Invoke(parking, [map.Grid.Owner, map.Grid.Owner, 0]))!.InnerException,
                Is.TypeOf<InvalidOperationException>());
            var paid = entities.System<SharedMapSystem>().CreateGridEntity(map.MapId);
            parking.RegisterPurchasedShip(paid.Owner, Guid.NewGuid(), 100, "same-vessel", "Captain");
            Assert.That(parking.IsVoucherPurchasedShuttle(paid.Owner), Is.False,
                "Neither matching vessel nor matching price is evidence of voucher origin.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StoredVoucherSaleIsDurableIdempotentAndKeepsThePaidVesselOfTheSameType()
    {
        var directory = Path.Combine(".local", "codex", "parking-origin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "parking.db");
        var owner = Guid.NewGuid();
        Guid voucherSlot, ordinarySlot;
        var grid = "grids: [1]\nentities: []\n";
        var voucher = new StoredShuttle(Guid.NewGuid(), "Same", "Captain", 0, 100, grid, ParkingStore.Hash(grid),
            PurchasedWithVoucher: true, OriginFaction: "TSF", OwnerProfile: 42, Vessel: "same-vessel");
        var ordinary = voucher with { Identity = Guid.NewGuid(), PurchasedWithVoucher = false };
        using (var store = new ParkingStore(path))
        {
            voucherSlot = Guid.NewGuid(); ordinarySlot = Guid.NewGuid();
            store.GrantPurchase(owner, store.BeginPurchase(owner, voucherSlot, 0));
            store.GrantPurchase(owner, store.BeginPurchase(owner, ordinarySlot, 0));
            store.Save(owner, voucherSlot, voucher); store.FinishSave(owner, voucherSlot);
            store.Save(owner, ordinarySlot, ordinary); store.FinishSave(owner, ordinarySlot);
            var sale = new StoredVoucherSale(owner, voucherSlot, 0, voucher);
            var receipt = new ParkingPayout(voucher.Identity, owner, 0, 9876, 42);
            Assert.That(await store.CommitStoredVoucherSaleAsync(sale, receipt), Is.True);
            Assert.That(await store.CommitStoredVoucherSaleAsync(sale, receipt), Is.False);
        }
        using (var reopened = new ParkingStore(path))
        {
            Assert.That(reopened.Slot(owner, voucherSlot).Ship, Is.Null);
            Assert.That(reopened.Slot(owner, ordinarySlot).Ship!.Identity, Is.EqualTo(ordinary.Identity));
            var receipt = reopened.PendingPayouts().Single();
            Assert.That(receipt.Id, Is.EqualTo(voucher.Identity));
            Assert.That(receipt.Amount, Is.EqualTo(9876));
            Assert.That(receipt.Owner, Is.EqualTo(owner));
            Assert.That(receipt.ProfileId, Is.EqualTo(42));
        }
    }

    [Test]
    public void LegacyVoucherEvidenceMustBelongToTheSavedRoot()
    {
        string Grid(bool rootVoucher) => $"""
            grids: [1]
            entities:
            - proto: ''
              entities:
              - uid: 1
                components:
                - type: ParkingShuttle
                  vessel: Evidence
                  purchasedWithVoucher: {rootVoucher.ToString().ToLowerInvariant()}
              - uid: 2
                components:
                - type: ShuttleDeed
                  purchasedWithVoucher: true
            """;
        StoredShuttle Saved(string grid) => new(Guid.NewGuid(), "Ship", "Owner", 0, 100, grid, ParkingStore.Hash(grid));
        Assert.That(ParkingStore.ReadStoredOrigin(Saved(Grid(false))).Voucher, Is.False,
            "A passenger's voucher or deed does not make the ship a voucher purchase.");
        Assert.That(ParkingStore.ReadStoredOrigin(Saved(Grid(true))).Voucher, Is.True);
        Assert.That(ParkingStore.ReadStoredOrigin(Saved("grids: [1]\nentities: []")).Voucher, Is.False);
    }

    internal static void BindFixtureOrigin(IEntityManager entities, EntityUid actor, EntityUid grid)
    {
        var identity = entities.GetComponent<BankCharacterComponent>(actor);
        var minds = entities.System<SharedMindSystem>();
        EntityUid mind;
        if (entities.TryGetComponent<MindContainerComponent>(actor, out var container) && container.Mind is { } existing)
            mind = existing;
        else if (entities.TryGetComponent<ActorComponent>(actor, out var attached))
            mind = entities.System<MindSystem>().GetOrCreateMind(attached.PlayerSession.UserId).Owner;
        else
            mind = minds.CreateMind(null).Owner;
        minds.TransferTo(mind, actor);
        var roles = entities.System<SharedRoleSystem>();
        roles.MindRemoveRole<JobRoleComponent>(mind);
        roles.MindAddJobRole(mind, silent: true, jobPrototype: "Contractor");
        var ship = entities.GetComponent<ParkingShuttleComponent>(grid);
        ship.OwnerAccount = identity.User;
        ship.ParkingProfile = identity.Profile;
        ship.OriginFaction = entities.System<ParkingSystem>().GetParkingOriginFaction(actor);
    }
}
