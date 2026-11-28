using Robust.Shared.Maths;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server._DeepLagoon.Voyages;
using Content.Server._NF.Bank;
using Content.Server.Database;
using Content.Server.Power.Components;
using Content.Server.Preferences.Managers;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._Mono.Ships;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;
namespace Content.IntegrationTests.Tests._DeepLagoon;
[TestFixture, NonParallelizable]
public sealed class SectorExpeditionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static T Complete<T>(ITaskManager tasks, Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
    private static void Complete(ITaskManager tasks, Task task) { tasks.BlockWaitOnTask(task); task.GetAwaiter().GetResult(); }
    [TestCase(true)]
    [TestCase(false)]
    public async Task HumanFlightIsPublicUsesAnEmptyOrbitCompletesNativePlanetAndReturnsDeadCrewWithoutReviving(bool hasDrive)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server; var e = server.EntMan; var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("sector_captain");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(player.UserId, out _));
        var db = server.ResolveDependency<IServerDbManager>(); var tasks = server.ResolveDependency<ITaskManager>();
        var voyages = e.System<VoyageSystem>(); var sectors = e.System<SectorExpeditionSystem>();
        EntityUid actor = default, console = default, planetMap = default, orbit = default;
        EconomyAccount before = null!; var published = Guid.NewGuid();
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true); server.CfgMan.SetCVar(SectorExpeditionCVars.Enabled, true);
            Set(sectors, "_nextAutoOrders", TimeSpan.MaxValue);
            var maps = e.System<SharedMapSystem>(); var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = -3; x < 5; x++) for (var y = -3; y < 5; y++) maps.SetTile(map.Grid, new Vector2i(x, y), tile);
            e.EnsureComponent<ShuttleComponent>(map.Grid); e.EnsureComponent<SalvageExpeditionDataComponent>(map.Grid).Enabled = true;
            e.EnsureComponent<ShipOwnershipComponent>(map.Grid).OwnerUserId = player.UserId;
            actor = e.SpawnEntity(hasDrive ? "MobHuman" : "MobHumanDummy", map.GridCoords); e.EnsureComponent<MobStateComponent>(actor); e.EnsureComponent<BankAccountComponent>(actor);
            e.EnsureComponent<Content.Shared.Damage.Components.GodmodeComponent>(actor);
            server.PlayerMan.SetAttachedEntity(player, actor);
            before = Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, prefs.GetPreferences(player.UserId).SelectedCharacterIndex));
            e.System<BankSystem>().SetConfirmedBankBalance(actor, before.Bank);
            console = e.SpawnEntity("ComputerSalvageExpedition", new EntityCoordinates(map.Grid, 1, 1));
            var cp = e.GetComponent<ApcPowerReceiverComponent>(console); cp.NeedsPower = false; cp.Powered = true;
            string Why() => (string)typeof(SectorExpeditionSystem).GetMethod("DepartureBlockReason", Private)!.Invoke(sectors, [player, console, map.Grid.Owner, before, "echo", new[] { "colossus", "helios", "echo" }])!;
            Assert.That(Why(), Is.Empty, "Missing drive uses the slower expedition transit.");
            var drive = e.SpawnEntity("MachineFTLDrive", new EntityCoordinates(map.Grid, 3, 3));
            var dp = e.GetComponent<ApcPowerReceiverComponent>(drive); dp.NeedsPower = true; dp.Powered = false;
            Assert.That(Why(), Is.EqualTo("БСС-двигатель не получает питание"));
            dp.NeedsPower = false; dp.Powered = true;
            e.GetComponent<FTLDriveComponent>(drive).Cooldown = 0;
            e.System<SharedTransformSystem>().AnchorEntity(console); e.System<SharedTransformSystem>().AnchorEntity(drive);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, SalvageConsoleUiKey.Expedition, actor);
            Assert.That(Why(), Is.Empty);
            if (!hasDrive) e.DeleteEntity(drive);
        });
        async Task Wait(Func<bool> condition, int attempts = 1500)
        {
            for (var i = 0; i < attempts; i++)
            {
                var done = false;
                await server.WaitAssertion(() => { Set(sectors, "_next", TimeSpan.Zero); done = condition(); });
                if (done) return;
                await server.WaitRunTicks(20); await Task.Delay(10);
            }
            Assert.Fail("The real sector/FTL transition did not complete.");
        }
        async Task Request(SectorExpeditionAction action, int planet = -1, string sector = "echo", Guid? order = null)
        {
            await Task.Delay(300);
            await server.WaitAssertion(() =>
            {
                e.System<SharedUserInterfaceSystem>().OpenUi(console, SalvageConsoleUiKey.Expedition, actor);
                var request = new SectorExpeditionRequest { Console = e.GetNetEntity(console), Action = action, Sector = sector, Operation = published, Order = order ?? published, Price = 6000, Planet = planet };
                typeof(SectorExpeditionSystem).GetMethod("OnRequest", Private)!.Invoke(sectors, [request, new EntitySessionEventArgs(player)]);
            });
            await Wait(() => !Field<HashSet<Guid>>(sectors, "_busy").Contains(player.UserId.UserId));
        }
        await Wait(() => voyages.Ready);
        await Request(SectorExpeditionAction.PostResearch);
        await server.WaitAssertion(() =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Complete(tasks, db.SectorEncounterAsync(voyages.WorldSnapshot.Cycle, "echo", ["colossus", "helios", "echo"], 0, now, Guid.Empty, -1));
            Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, before.Slot)).Bank, Is.EqualTo(before.Bank - 6000));
        });
        await Request(SectorExpeditionAction.Depart);
        await server.WaitAssertion(() => Assert.That(e.HasComponent<FTLComponent>(map.Grid), Is.False, "A valid but unaccepted order must not authorize travel."));
        await Request(SectorExpeditionAction.Depart, sector: "helios");
        await server.WaitAssertion(() => Assert.That(e.EntityQuery<SectorExpeditionMapComponent>(), Is.Empty, "Transit-only nodes must not create physical maps."));
        await Request(SectorExpeditionAction.AcceptResearch);
        await server.WaitAssertion(() =>
        {
            var ftl = e.GetComponent<FTLComponent>(map.Grid);
            Assert.That(ftl.StartupTime + ftl.TravelTime, Is.LessThanOrEqualTo(hasDrive ? 20 : 80));
            if (!hasDrive) Assert.That(ftl.StartupTime + ftl.TravelTime, Is.EqualTo(SectorResearchDefinition.FlightTime(voyages.RouteGraph.Minutes(["colossus", "helios", "echo"]), 5.5f, 20f, hasDrive: false)).Within(.01));
            Assert.That(sectors.PlayerLocation(actor).Flight, Is.Not.EqualTo(Guid.Empty));
            Assert.That(ftl.ArrivalTimeOverride, Is.Not.Null);
            var publicFlight = voyages.PublicFlights(Guid.NewGuid(), 999).Single(f => f.Kind == 4);
            Assert.That(publicFlight.Mine, Is.False); Assert.That(publicFlight.Stops, Is.EqualTo(new[] { "colossus", "helios", "echo" }));
            var own = voyages.PublicFlights(player.UserId.UserId, before.Profile).Single(f => f.Kind == 4); Assert.That(own.Mine, Is.True);
        });
        await Wait(() => e.GetComponent<TransformComponent>(map.Grid).MapUid is { } current && e.HasComponent<SectorExpeditionMapComponent>(current));
        await server.WaitAssertion(() =>
        {
            orbit = e.GetComponent<TransformComponent>(map.Grid).MapUid!.Value;
            Assert.That(e.HasComponent<Content.Shared._DeepLagoon.SpaceEnvironment.SpaceEnvironmentComponent>(orbit), Is.False);
            Assert.That(e.HasComponent<Content.Shared.Parallax.Biomes.BiomeComponent>(orbit), Is.False);
            var navigation = e.GetComponent<Content.Shared._FarHorizons.StarSystem.StarSystemMapComponent>(orbit);
            Assert.That(navigation.Definition!.NavigationOnly, Is.True); Assert.That(navigation.Definition.Regions, Is.Empty);
            Assert.That(navigation.StarSystem, Is.Not.Null); Assert.That(navigation.StarSystem!.Planets.Count, Is.EqualTo(SectorResearchDefinition.Count("echo", voyages.WorldSnapshot.Seed)));
            Assert.That(navigation.Definition.Planets.All(p => !string.IsNullOrWhiteSpace(p.Name)), Is.True);
            Assert.That(sectors.PlayerLocation(actor).Sector, Is.EqualTo("echo"));
            Assert.That(e.EntityQuery<SectorExpeditionMapComponent>().Count(), Is.EqualTo(1), "Intermediate sectors must not produce physical maps.");
            Assert.That(e.EntityQuery<SalvageExpeditionComponent>().Any(), Is.False, "Approaching a planet must not generate its surface.");
            Assert.That(voyages.PublicFlights(Guid.NewGuid(), 0).Any(f => f.Kind == 4), Is.False);
        });
        await Wait(() => !e.HasComponent<FTLComponent>(map.Grid));
        await Wait(() => Complete(tasks, db.ReadSectorResearchAsync(voyages.WorldSnapshot.Cycle, Guid.Empty, 0)).Orders.Any(o => o.Automatic && o.Sector == "echo"));
        await server.WaitAssertion(() =>
        {
            var position = typeof(SectorExpeditionSystem).GetMethod("PlanetPosition", Private)!.Invoke(sectors, ["echo", 0]);
            e.System<SharedTransformSystem>().SetCoordinates(map.Grid, new EntityCoordinates(orbit, (Vector2)position!));
        });
        Guid other = default;
        await server.WaitAssertion(() => other = Complete(tasks, db.ReadSectorResearchAsync(voyages.WorldSnapshot.Cycle, Guid.Empty, 0)).Orders.First(o => o.Sector == "frontier").Id);
        await Request(SectorExpeditionAction.AcceptResearch, sector: "frontier", order: other);
        await Request(SectorExpeditionAction.Depart, sector: "frontier", order: other);
        await server.WaitAssertion(() => Assert.That(e.HasComponent<FTLComponent>(map.Grid), Is.False, "An active assignment cannot be replaced or bypassed by another funded order."));
        await Request(SectorExpeditionAction.Land, 0);
        await Wait(() => e.GetComponent<TransformComponent>(map.Grid).MapUid is { } current && e.HasComponent<SalvageExpeditionComponent>(current));
        await Wait(() => !e.HasComponent<FTLComponent>(map.Grid));
        await server.WaitAssertion(() =>
        {
            planetMap = e.GetComponent<TransformComponent>(map.Grid).MapUid!.Value;
            var mission = e.GetComponent<SalvageExpeditionComponent>(planetMap);
            Assert.That(mission.Station, Is.EqualTo(map.Grid.Owner));
            Assert.That(mission.ReturnLocations[map.Grid.Owner].Coordinates.EntityId, Is.EqualTo(orbit));
            // Emulate the native objective system's completion signal, never a client-controlled flag.
            mission.Completed = true; mission.ObjectivesCompleted = mission.ObjectivesTotal;
        });
        await Request(SectorExpeditionAction.LeavePlanet);
        await Wait(() => e.GetComponent<TransformComponent>(map.Grid).MapUid == orbit);
        await Wait(() => !e.HasComponent<FTLComponent>(map.Grid));
        await Wait(() => Complete(tasks, db.ReadVoyageWorldAsync(false, [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Research.GetValueOrDefault("echo")?.Completed.Contains(0) == true);
        await server.WaitAssertion(() =>
        {
            var count = SectorResearchDefinition.Count("echo", voyages.WorldSnapshot.Seed);
            var expected = before.Bank - 6000 + (6000 + count - 1) / count + (SectorResearchDefinition.Pool("echo", voyages.WorldSnapshot.Seed) + count - 1) / count;
            Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, before.Slot)).Bank, Is.EqualTo(expected));
        });
        if (hasDrive)
        {
        // Real syndicate hulls, real guards and the existing drone steering/fire systems.
        EntityUid pirateMap = default; object pirateTrip = null!; TimeSpan reactionAt = default;
        await server.WaitAssertion(() =>
        {
            // Keep this tiny, unarmoured fixture intact while observing the real targeting and guns.
            // Otherwise the three full-size pirate hulls can collide with it before the assertion runs.
            e.System<Robust.Shared.Physics.Systems.SharedPhysicsSystem>().SetCanCollide(map.Grid.Owner, false);
            e.EnsureComponent<Content.Shared.Damage.Components.GodmodeComponent>(actor);
            var cache = Field<Dictionary<string, int>>(sectors, "_combatCatalogue");
            foreach (var vessel in server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().EnumeratePrototypes<Content.Shared._NF.Shipyard.Prototypes.VesselPrototype>()) cache[vessel.ID] = 0;
            cache["Hunter"] = 40;
            Complete(tasks, db.SectorEncounterAsync(voyages.WorldSnapshot.Cycle, "frontier", ["colossus", "boreas", "frontier"], 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Guid.Empty, -1));
            var encounter = typeof(SectorExpeditionSystem).GetMethod("GetOrbit", Private)!.Invoke(sectors, ["frontier"])!;
            pirateMap = (EntityUid)encounter.GetType().GetField("Map")!.GetValue(encounter)!;
            e.System<SharedTransformSystem>().SetCoordinates(map.Grid, new EntityCoordinates(pirateMap, Vector2.Zero));
            typeof(SectorExpeditionSystem).GetMethod("UpdatePirates", Private)!.Invoke(sectors, null);
            pirateTrip = ((System.Collections.IDictionary)typeof(SectorExpeditionSystem).GetField("_trips", Private)!.GetValue(sectors)!)[map.Grid.Owner]!;
            reactionAt = (TimeSpan)pirateTrip.GetType().GetField("PirateAttackAt")!.GetValue(pirateTrip)!;
            Assert.That(reactionAt - server.ResolveDependency<IGameTiming>().CurTime, Is.EqualTo(TimeSpan.FromSeconds(10)));
        });
        await Wait(() => !Field<HashSet<string>>(sectors, "_encounterBusy").Contains("frontier"));
        EntityUid[] cores = []; EntityUid[] guns = [];
        await server.WaitAssertion(() =>
        {
            // Targeting is attached by the first AI update; select the actual controller prototype before then.
            cores = e.EntityQuery<MetaDataComponent, TransformComponent>().Where(v => v.Item2.MapUid == pirateMap && v.Item1.EntityPrototype?.ID == "NpcStationAiAttackerSmart").Select(v => v.Item1.Owner).ToArray();
            Assert.That(cores, Has.Length.EqualTo(3));
            foreach (var core in cores)
            {
                var pirateGrid = e.GetComponent<TransformComponent>(core).GridUid!.Value;
                var iff = e.GetComponent<IFFComponent>(pirateGrid);
                Assert.That(iff.Color, Is.EqualTo(Color.Red));
                Assert.That((iff.Flags & IFFFlags.AlwaysShowColor) != 0, Is.True, "An anonymous radar signature must keep its red colour.");
                Assert.That((iff.Flags & (IFFFlags.Hide | IFFFlags.HideLabelAlways | IFFFlags.HideLabel)), Is.EqualTo(IFFFlags.None));
            }
            var guards = e.EntityQuery<MobStateComponent, TransformComponent>().Where(v => v.Item2.MapUid == pirateMap && v.Item1.Owner != actor).Select(v => v.Item1.Owner).ToArray();
            Assert.That(guards.Length, Is.InRange(9, 27));
            foreach (var guard in guards)
            {
                Assert.That(e.HasComponent<Content.Server.Cargo.Components.CargoSellBlacklistComponent>(guard), Is.True);
                foreach (var organ in e.System<Content.Server.Body.Systems.BodySystem>().GetBodyOrgans(guard))
                    Assert.That(e.HasComponent<Content.Server.Cargo.Components.CargoSellBlacklistComponent>(organ.Id), Is.True);
            }
            guns = e.EntityQuery<Content.Server._Mono.FireControl.FireControllableComponent, TransformComponent>().Where(v => v.Item2.MapUid == pirateMap).Select(v => v.Item1.Owner).ToArray();
            Assert.That(guns.Length, Is.GreaterThanOrEqualTo(3));
            var transform = e.System<SharedTransformSystem>(); var position = transform.GetWorldPosition(guns[0]) + transform.GetWorldRotation(guns[0]).ToVec() * 80;
            transform.SetCoordinates(map.Grid, new EntityCoordinates(pirateMap, position));
        });
        await Wait(() =>
        {
            Assert.That(cores.Any(c => e.HasComponent<Content.Server._Mono.NPC.HTN.ShipTargetingComponent>(c) || e.HasComponent<Content.Server._Mono.NPC.HTN.ShipSteererComponent>(c)), Is.False,
                "Pirates must neither pursue nor aim at the arriving ship during its reaction period.");
            Assert.That(guns.Any(g => e.TryGetComponent<Content.Shared.Weapons.Ranged.Components.GunComponent>(g, out var gun) && gun.LastFire > TimeSpan.Zero), Is.False);
            return server.ResolveDependency<IGameTiming>().CurTime >= reactionAt - TimeSpan.FromSeconds(1);
        }, 100);
        await server.WaitAssertion(() => Assert.That(server.ResolveDependency<IGameTiming>().CurTime, Is.LessThan(reactionAt)));
        await Wait(() => cores.All(c => e.HasComponent<Content.Server._Mono.NPC.HTN.ShipSteererComponent>(c) && e.HasComponent<Content.Server._Mono.NPC.HTN.ShipTargetingComponent>(c)));
        await server.WaitAssertion(() => Assert.That(server.ResolveDependency<IGameTiming>().CurTime, Is.GreaterThanOrEqualTo(reactionAt)));
        await Wait(() => guns.Any(g => e.TryGetComponent<Content.Shared.Weapons.Ranged.Components.GunComponent>(g, out var gun) && gun.LastFire > TimeSpan.Zero), 100);
        await server.WaitAssertion(() =>
        {
            var register = typeof(SectorExpeditionSystem).GetMethod("RegisterPirateArrival", Private)!;
            register.Invoke(sectors, [pirateTrip, pirateMap]);
            Assert.That((TimeSpan)pirateTrip.GetType().GetField("PirateAttackAt")!.GetValue(pirateTrip)!, Is.EqualTo(reactionAt), "Returning to the same orbit never restarts the pause.");
            // Emulate another first arrival to test escalation through an actual successful gunshot.
            pirateTrip.GetType().GetField("PirateAlertMap")!.SetValue(pirateTrip, null);
            register.Invoke(sectors, [pirateTrip, pirateMap]);
            reactionAt = (TimeSpan)pirateTrip.GetType().GetField("PirateAttackAt")!.GetValue(pirateTrip)!;
            typeof(SectorExpeditionSystem).GetMethod("UpdatePirates", Private)!.Invoke(sectors, null);
            Assert.That(cores.Any(c => e.HasComponent<Content.Server._Mono.NPC.HTN.ShipTargetingComponent>(c)), Is.False);
            var mount = new EntityCoordinates(map.Grid, 2.5f, .5f);
            e.SpawnEntity("HardpointBallisticLight", mount);
            var weapon = e.SpawnEntity("WeaponTurretL85Autocannon", mount);
            var slots = e.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();
            Assert.That(slots.TryEject(weapon, "gun_magazine", null, out var defaultLoader), Is.True);
            e.DeleteEntity(defaultLoader!.Value);
            var loader = e.SpawnEntity("Magazine20mmAS", mount);
            Assert.That(slots.TryInsert(weapon, "gun_magazine", loader, null), Is.True);
            var ammoBefore = new Content.Shared.Weapons.Ranged.Events.GetAmmoCountEvent();
            e.EventBus.RaiseLocalEvent(weapon, ref ammoBefore);
            Assert.That(ammoBefore.Count, Is.Positive);
            // A completed arrival can still carry the native FTL cooldown component.
            var cooldown = e.AddComponent<FTLComponent>(map.Grid.Owner);
            cooldown.State = Content.Shared.Shuttles.Systems.FTLState.Cooldown;
            e.System<Content.Shared.Weapons.Ranged.Systems.SharedGunSystem>().AttemptShoot(weapon,
                e.GetComponent<Content.Shared.Weapons.Ranged.Components.GunComponent>(weapon));
            var ammoAfter = new Content.Shared.Weapons.Ranged.Events.GetAmmoCountEvent();
            e.EventBus.RaiseLocalEvent(weapon, ref ammoAfter);
            Assert.That(ammoAfter.Count, Is.EqualTo(ammoBefore.Count - 1), "Escalation uses a real shot with actual ammunition consumption.");
            Assert.That((TimeSpan)pirateTrip.GetType().GetField("PirateAttackAt")!.GetValue(pirateTrip)!, Is.EqualTo(TimeSpan.Zero));
            Assert.That(server.ResolveDependency<IGameTiming>().CurTime, Is.LessThan(reactionAt));
            typeof(SectorExpeditionSystem).GetMethod("UpdatePirates", Private)!.Invoke(sectors, null);
            Assert.That(cores.All(c => e.HasComponent<Content.Server._Mono.NPC.HTN.ShipTargetingComponent>(c)), Is.True,
                "Opening fire must activate the normal pirate AI without waiting for the remaining grace period.");
            e.RemoveComponent<FTLComponent>(map.Grid.Owner);
        });
        await server.WaitAssertion(() => { foreach (var core in cores) e.DeleteEntity(core); });
        await Wait(() => Complete(tasks, db.ReadVoyageWorldAsync(false, [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Research.GetValueOrDefault("frontier")?.DefeatedPirates.Count == 3);
        }
        await server.WaitAssertion(() =>
        {
            e.System<SharedTransformSystem>().SetCoordinates(map.Grid, new EntityCoordinates(orbit, Vector2.Zero));
            if (hasDrive)
            {
                e.System<MobStateSystem>().ChangeMobState(actor, MobState.Dead);
                // Reproduce a dead, unbuckled body thrown off a damaged hull by an explosion.
                e.System<SharedTransformSystem>().SetCoordinates(actor, new EntityCoordinates(orbit, new Vector2(490, -660)));
                e.System<Robust.Shared.Physics.Systems.SharedPhysicsSystem>().SetLinearVelocity(actor, new Vector2(90, -110));
            }
        });
        if (!hasDrive) await Request(SectorExpeditionAction.AbandonResearch);
        await Wait(() => e.GetComponent<TransformComponent>(map.Grid).MapUid == map.MapUid);
        await server.WaitAssertion(() =>
        {
            Assert.That(e.GetComponent<TransformComponent>(actor).GridUid, Is.EqualTo(map.Grid.Owner));
            Assert.That(e.GetComponent<MobStateComponent>(actor).CurrentState, Is.EqualTo(hasDrive ? MobState.Dead : MobState.Alive), "Emergency retrieval must not revive or duplicate the character.");
            Assert.That(voyages.PublicFlights(Guid.NewGuid(), 0).Any(f => f.Kind == 4), Is.False);
            Assert.That(sectors.PlayerLocation(actor).Sector, Is.EqualTo(VoyageRouteModel.Home));
            if (hasDrive)
            {
                Assert.That(e.GetComponent<TransformComponent>(actor).MapUid, Is.EqualTo(map.MapUid));
                Assert.That(e.System<Robust.Shared.Containers.SharedContainerSystem>().IsEntityInContainer(actor), Is.False);
                Assert.That(e.GetComponent<Robust.Shared.Physics.Components.PhysicsComponent>(actor).LinearVelocity.Length(), Is.LessThan(.01f));
            }
        });
        if (hasDrive)
        {
            await pair.RunSeconds(5);
            await server.WaitAssertion(() => Assert.That(e.GetComponent<TransformComponent>(actor).MapUid, Is.EqualTo(map.MapUid), "The body must remain home after FTL/cooldown completes."));
        }
        if (!hasDrive) await Request(SectorExpeditionAction.AcceptResearch, sector: "frontier", order: other);
        await pair.CleanReturnAsync();
    }
}
