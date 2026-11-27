#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.IntegrationTests.Pair;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Mono.GameRule.Systems;
using Content.Server._Mono.Ships.Systems;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class FleetQuotaTests
{
    private sealed class FleetPurchaseProbeSystem : EntitySystem
    {
        public AttemptShipyardShuttlePurchaseEvent Attempt(EntityUid candidate, VesselPrototype vessel)
        {
            var attempt = new AttemptShipyardShuttlePurchaseEvent(candidate, candidate, vessel);
            RaiseLocalEvent(ref attempt);
            return attempt;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PoweredOffInactiveVesselsStillOccupyQuotaIncludingPausedParking(bool paused)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var vessel = await Ready(pair);
        var source = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Owned(entities, source.Grid.Owner, vessel);
            var console = entities.SpawnEntity(null, new EntityCoordinates(source.Grid.Owner, Vector2.Zero));
            entities.EnsureComponent<ShuttleConsoleComponent>(console);
            entities.EnsureComponent<ApcPowerReceiverComponent>(console).PowerDisabled = true;
            var inactive = entities.EnsureComponent<ShipActivityComponent>(source.Grid.Owner);
            inactive.InactivePastThreshold = true;
            inactive.InactiveLastCheck = true;
            inactive.TimesInactive = 999;
            entities.System<SharedMapSystem>().SetPaused(source.MapId, paused);
            Assert.That(Attempt(entities, vessel).Cancelled, Is.True,
                "Switching off or parking a ship must not release its faction fleet slot.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LoadedPurchaseCandidateDoesNotCountItself()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var vessel = await Ready(pair);
        var source = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Owned(entities, source.Grid.Owner, vessel);
            Assert.That(entities.System<FleetPurchaseProbeSystem>().Attempt(source.Grid.Owner, vessel).Cancelled, Is.False,
                "Purchase validation runs after loading the candidate; it must count only the existing fleet.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StableIdentityDeduplicatesPhysicalAndCrashRecoveredCopiesAndRestoreExcludesItself()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var vessel = await Ready(pair);
        var first = await pair.CreateTestMap();
        var duplicate = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var identity = Guid.NewGuid();
            Owned(entities, first.Grid.Owner, vessel, identity);
            Owned(entities, duplicate.Grid.Owner, vessel, identity);
            var snapshot = new FreeParkingSnapshot(identity, Guid.NewGuid(), 0, Guid.NewGuid(), "Test", "Owner", 0, 1, "",
                Profile: 123, Vessel: vessel.ID);
            var parking = entities.System<ParkingSystem>();
            var recovered = (Dictionary<Guid, FreeParkingSnapshot>)typeof(ParkingSystem)
                .GetField("_freeParkingRecovered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parking)!;
            recovered.Add(identity, snapshot);

            vessel.LimitActive = 2;
            Assert.That(Attempt(entities, vessel).Cancelled, Is.False, "One logical ship must consume one slot across all representations.");
            vessel.LimitActive = 1;
            Assert.That(Attempt(entities, vessel).Cancelled, Is.True);
            var restrictions = entities.System<ShuttleRestrictionsSystem>();
            Assert.That(restrictions.CanRestoreParkedVessel(vessel.ID, identity), Is.True,
                "Returning the same identity must not block itself on a reservation or physical copy.");
            Assert.That(restrictions.CanRestoreParkedVessel(vessel.ID, Guid.NewGuid()), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LongTermStorageDoesNotReserveFleetButRestorationRequiresFreeSlot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var vessel = await Ready(pair);
        var live = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var parking = entities.System<ParkingSystem>();
            var store = (ParkingStore)typeof(ParkingSystem).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parking)!;
            var owner = Guid.NewGuid();
            var slot = store.BeginPurchase(owner, Guid.NewGuid(), 0);
            store.GrantPurchase(owner, slot);
            const string yaml = "meta: { format: 7 }\nentities: []\n";
            var stored = new StoredShuttle(Guid.NewGuid(), "Stored", "Owner", 0, 1, yaml, ParkingStore.Hash(yaml),
                OwnerProfile: 123, OriginFaction: "TestFaction", Vessel: vessel.ID);
            store.Save(owner, slot.Id, stored);
            store.FinishSave(owner, slot.Id);
            Assert.That(store.Slot(owner, slot.Id).Ship!.Identity, Is.EqualTo(stored.Identity));
            Assert.That(Attempt(entities, vessel).Cancelled, Is.False, "Offline long-term storage must not monopolize active fleet slots.");
            var restrictions = entities.System<ShuttleRestrictionsSystem>();
            Assert.That(restrictions.CanRestoreParkedVessel(vessel.ID, stored.Identity), Is.True);
            Owned(entities, live.Grid.Owner, vessel, Guid.NewGuid());
            Assert.That(Attempt(entities, vessel).Cancelled, Is.True);
            Assert.That(restrictions.CanRestoreParkedVessel(vessel.ID, stored.Identity), Is.False,
                "A stored ship cannot be activated over an occupied fleet limit.");
            Assert.Throws<InvalidOperationException>(() => parking.CheckParkingFleetLimit(vessel.ID, stored.Identity));
            entities.DeleteEntity(live.Grid.Owner);
            Assert.That(restrictions.CanRestoreParkedVessel(vessel.ID, stored.Identity), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PrivateStagingClonesDoNotReserveFleet(bool markerOnMap)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var vessel = await Ready(pair);
        var stage = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Owned(entities, stage.Grid.Owner, vessel, Guid.NewGuid());
            entities.EnsureComponent<ParkingStagingSnapshotComponent>(markerOnMap ? stage.MapUid : stage.Grid.Owner);
            Assert.That(entities.System<ParkingSystem>().IsParkingStagingGrid(stage.Grid.Owner), Is.True);
            Assert.That(Attempt(entities, vessel).Cancelled, Is.False,
                "A private restore or appraisal clone must not appear in the deployed fleet.");
            Assert.That(entities.System<ShuttleRestrictionsSystem>().CanRestoreParkedVessel(vessel.ID, Guid.NewGuid()), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    private static void Owned(IEntityManager entities, EntityUid grid, VesselPrototype vessel, Guid? identity = null)
    {
        entities.EnsureComponent<VesselComponent>(grid).VesselId = vessel.ID;
        // Fixture ownership without invoking the full shipyard purchase/deed initialization path.
        var deed = entities.EnsureComponent<ShuttleDeedComponent>(grid);
        typeof(ShuttleDeedComponent).GetField("DeedHolder", BindingFlags.Instance | BindingFlags.Public)!.SetValue(deed, grid);
        if (identity is not { } stable) return;
        var parking = entities.EnsureComponent<ParkingShuttleComponent>(grid);
        parking.Identity = stable;
        parking.Vessel = vessel.ID;
        parking.OwnerAccount = Guid.NewGuid();
    }

    private static AttemptShipyardShuttlePurchaseEvent Attempt(IEntityManager entities, VesselPrototype vessel) =>
        entities.System<FleetPurchaseProbeSystem>().Attempt(EntityUid.Invalid, vessel);

    private static async Task<VesselPrototype> Ready(TestPair pair)
    {
        var ready = false;
        for (var i = 0; i < 200 && !ready; i++)
        {
            await pair.RunTicksSync(1);
            await pair.Server.WaitPost(() => ready = pair.Server.EntMan.System<ParkingSystem>()
                .TryCopyStoredVesselReservations(new Dictionary<Guid, string>(), includeLongTerm: false));
        }
        VesselPrototype vessel = null!;
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(ready, Is.True, "Wait for the real parking reservation index before quota checks.");
            pair.Server.EntMan.System<HyperwarRuleSystem>().HyperwarActive = false;
            vessel = pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<VesselPrototype>().First();
            vessel.LimitActive = 1;
            vessel.HyperwarLimitActive = 1;
            vessel.HyperwarTimelock = TimeSpan.Zero;
        });
        return vessel;
    }
}
