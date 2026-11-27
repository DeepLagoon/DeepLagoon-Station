using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Voyages;
using Content.Server._DeepLagoon.Economy;
using Content.Shared._Mono.Ships;
using Content.Server._NF.Bank;
using Content.Server.Database;
using Content.Server.Power.Components;
using Content.Server.Preferences.Managers;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Timing;
namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SectorCooperativeLandingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object system, string name, object value) => system.GetType().GetField(name, Private)!.SetValue(system, value);
    private static T Complete<T>(ITaskManager tasks, Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
    [TestCase(false)]
    [TestCase(true)]
    public async Task TwoShipsShareOnePlanetUseDifferentLandingFootprintsAndDivideOneResearchReward(bool localPlanet)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server; var e = server.EntMan; var home = await pair.CreateTestMap();
        var players = new[] { await server.AddDummySession("coop_first"), await server.AddDummySession("coop_second") };
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => players.All(p => prefs.TryGetCachedPreferences(p.UserId, out _)));
        var tasks = server.ResolveDependency<ITaskManager>(); var db = server.ResolveDependency<IServerDbManager>();
        var sectors = e.System<SectorExpeditionSystem>(); var voyages = e.System<VoyageSystem>();
        var ships = new EntityUid[2]; var actors = new EntityUid[2]; var consoles = new EntityUid[2]; var accounts = new EconomyAccount[2];
        var order = Guid.NewGuid(); EntityUid orbit = default, surface = default; TimeSpan deadline = default;
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true); server.CfgMan.SetCVar(SectorExpeditionCVars.Enabled, true);
            Set(sectors, "_nextAutoOrders", TimeSpan.MaxValue);
            Set(sectors, "_autoOrdersBusy", true);
            if (localPlanet)
            {
                var navigation = e.EnsureComponent<Content.Shared._FarHorizons.StarSystem.StarSystemMapComponent>(home.MapUid);
                navigation.System = "SystemKyphrus";
                navigation.Definition = new Content.Shared._DeepLagoon.SpaceEnvironment.SectorDefinition
                { Planets = [new() { Name = "Родная планета", Template = "PlanetTypeHabitable", X = 400 }, new() { Name = "Газовый гигант", Template = "PlanetTypeGasGiant", X = 800 }] };
            }
            var maps = e.System<Robust.Server.GameObjects.MapSystem>(); var transform = e.System<SharedTransformSystem>();
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var i = 0; i < 2; i++)
            {
                var grid = maps.CreateGridEntity(home.MapId); ships[i] = grid.Owner;
                for (var x = -4; x <= 5 + i * 5; x++) for (var y = -4; y <= 5; y++) maps.SetTile(grid.Owner, grid.Comp, new EntityCoordinates(grid.Owner, x, y), tile);
                transform.SetCoordinates(grid.Owner, new EntityCoordinates(home.MapUid, new Vector2(i * 200 + 100, 0)));
                e.EnsureComponent<ShuttleComponent>(grid.Owner); e.EnsureComponent<SalvageExpeditionDataComponent>(grid.Owner).Enabled = true;
                e.EnsureComponent<ShipOwnershipComponent>(grid.Owner).OwnerUserId = players[i].UserId;
                actors[i] = e.SpawnEntity("MobHumanDummy", new EntityCoordinates(grid.Owner, Vector2.Zero));
                e.EnsureComponent<MobStateComponent>(actors[i]); e.EnsureComponent<BankAccountComponent>(actors[i]); server.PlayerMan.SetAttachedEntity(players[i], actors[i]);
                accounts[i] = Complete(tasks, db.ReadEconomyAccountAsync(players[i].UserId.UserId, prefs.GetPreferences(players[i].UserId).SelectedCharacterIndex));
                e.System<BankSystem>().SetConfirmedBankBalance(actors[i], accounts[i].Bank);
                consoles[i] = e.SpawnEntity("ComputerSalvageExpedition", new EntityCoordinates(grid.Owner, 1, 1));
                var power = e.GetComponent<ApcPowerReceiverComponent>(consoles[i]); power.NeedsPower = false; power.Powered = true;
                var drive = e.SpawnEntity("MachineFTLDrive", new EntityCoordinates(grid.Owner, 3, 3));
                var dp = e.GetComponent<ApcPowerReceiverComponent>(drive); dp.NeedsPower = false; dp.Powered = true;
                e.GetComponent<FTLDriveComponent>(drive).Cooldown = 0;
                transform.AnchorEntity(consoles[i]); transform.AnchorEntity(drive);
            }
        });
        async Task Wait(Func<bool> condition)
        {
            for (var i = 0; i < 1500; i++)
            {
                var done = false; await server.WaitAssertion(() => { Set(sectors, "_next", TimeSpan.Zero); done = condition(); });
                if (done) return;
                await server.WaitRunTicks(20); await Task.Delay(10);
            }
            Assert.Fail("Cooperative expedition transition timed out");
        }
        async Task Request(int captain, SectorExpeditionAction action, int planet = 0)
        {
            await Task.Delay(300);
            await server.WaitAssertion(() =>
            {
                e.System<SharedTransformSystem>().SetCoordinates(actors[captain], e.GetComponent<TransformComponent>(consoles[captain]).Coordinates);
                e.System<SharedUserInterfaceSystem>().OpenUi(consoles[captain], SalvageConsoleUiKey.Expedition, actors[captain]);
                typeof(SectorExpeditionSystem).GetMethod("OnRequest", Private)!.Invoke(sectors,
                    [new SectorExpeditionRequest { Console = e.GetNetEntity(consoles[captain]), Action = action, Sector = "echo", Order = order, Planet = planet }, new EntitySessionEventArgs(players[captain])]);
            });
            await Wait(() => !((HashSet<Guid>)typeof(SectorExpeditionSystem).GetField("_busy", Private)!.GetValue(sectors)!).Contains(players[captain].UserId.UserId));
        }
        await Wait(() => voyages.Ready);
        if (!localPlanet)
        {
        await server.WaitAssertion(() =>
        {
            var a = accounts[0]; var count = SectorResearchDefinition.Count("echo", voyages.WorldSnapshot.Seed);
            Complete(tasks, db.PublishSectorResearchAsync(new(order, voyages.WorldSnapshot.Cycle, "echo", "Совместное исследование", 6000, 6000,
                players[0].UserId.UserId, a.Profile, a.Slot, 0, count, new())));
            Complete(tasks, db.SectorEncounterAsync(voyages.WorldSnapshot.Cycle, "echo", ["colossus", "helios", "echo"], 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Guid.Empty, -1));
        });
        for (var i = 0; i < 2; i++) { await Request(i, SectorExpeditionAction.AcceptResearch); }
        await Wait(() => ships.All(s => e.GetComponent<TransformComponent>(s).MapUid is { } m && e.HasComponent<SectorExpeditionMapComponent>(m) && !e.HasComponent<FTLComponent>(s)));
        await server.WaitAssertion(() =>
        {
            orbit = e.GetComponent<TransformComponent>(ships[0]).MapUid!.Value;
            var pos = (Vector2)typeof(SectorExpeditionSystem).GetMethod("PlanetPosition", Private)!.Invoke(sectors, ["echo", 0])!;
            for (var i = 0; i < 2; i++) e.System<SharedTransformSystem>().SetCoordinates(ships[i], new EntityCoordinates(orbit, pos + new Vector2(i * 40, 0)));
        });
        }
        else
        {
            orbit = home.MapUid;
            await Request(0, SectorExpeditionAction.Land, 1);
            await server.WaitAssertion(() =>
            {
                Assert.That(e.HasComponent<FTLComponent>(ships[0]), Is.False, "A gas giant must reject server-side landing.");
                Assert.That(e.EntityQuery<SalvageExpeditionComponent>(), Is.Empty);
                for (var i = 0; i < 2; i++) e.System<SharedTransformSystem>().SetCoordinates(ships[i], new EntityCoordinates(orbit, new Vector2(400 + i * 40, 0)));
                var views = (SectorPlanetView[])typeof(SectorExpeditionSystem).GetMethod("HomePlanetViews", Private)!.Invoke(sectors, [ships[0], null, orbit])!;
                Assert.That(views[0].CanLand, Is.True); Assert.That(views[1].Landable, Is.False); Assert.That(views[1].CanLand, Is.False);
                Assert.That(Complete(tasks, db.ReadSectorResearchAsync(voyages.WorldSnapshot.Cycle, players[0].UserId.UserId, accounts[0].Profile)).Orders, Is.Empty);
            });
        }
        var sessionKey = localPlanet ? VoyageRouteModel.Home + "/" + orbit : "echo";
        await Request(0, SectorExpeditionAction.Land);
        await Wait(() => !((HashSet<(string, int)>)typeof(SectorExpeditionSystem).GetField("_planetLaunching", Private)!.GetValue(sectors)!).Contains((sessionKey, 0)));
        await Wait(() => e.GetComponent<TransformComponent>(ships[0]).MapUid is { } map && e.HasComponent<SalvageExpeditionComponent>(map) && !e.HasComponent<FTLComponent>(ships[0]));
        await server.WaitAssertion(() =>
        {
            var mission = e.EntityQuery<SalvageExpeditionComponent>(true).Single();
            deadline = mission.EndTime = server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromHours(1);
            if (!localPlanet)
            {
                var orbitState = typeof(SectorExpeditionSystem).GetField("_orbits", Private)!.GetValue(sectors)!;
                var orbitObject = orbitState.GetType().GetProperty("Item")!.GetValue(orbitState, ["echo"])!;
                var marker = ((Dictionary<int, EntityUid>)orbitObject.GetType().GetField("Markers")!.GetValue(orbitObject)!)[0];
                Assert.That(e.GetComponent<TransformComponent>(marker).MapUid, Is.EqualTo(orbit), "A planet marker must not ride the first ship to its expedition.");
                Assert.That(e.GetComponent<TransformComponent>(marker).GridUid, Is.Null);
            }
            // The waiting vessel can drift or be moved by native orbital anti-collision while the first lands.
            var position = localPlanet ? new Vector2(400, 0) : (Vector2)typeof(SectorExpeditionSystem).GetMethod("PlanetPosition", Private)!.Invoke(sectors, ["echo", 0])!;
            e.System<SharedTransformSystem>().SetCoordinates(ships[1], new EntityCoordinates(orbit, position + new Vector2(40, 0)));
        });
        await Request(1, SectorExpeditionAction.Land);
        await Wait(() => ships.All(s => e.GetComponent<TransformComponent>(s).MapUid is { } m && e.HasComponent<SalvageExpeditionComponent>(m) && !e.HasComponent<FTLComponent>(s)));
        await server.WaitAssertion(() =>
        {
            surface = e.GetComponent<TransformComponent>(ships[0]).MapUid!.Value;
            Assert.That(e.GetComponent<TransformComponent>(ships[1]).MapUid, Is.EqualTo(surface));
            Assert.That(e.EntityQuery<SalvageExpeditionComponent>().Count(), Is.EqualTo(1));
            var first = e.System<SharedTransformSystem>().GetWorldMatrix(ships[0]).TransformBox(e.GetComponent<MapGridComponent>(ships[0]).LocalAABB);
            var second = e.System<SharedTransformSystem>().GetWorldMatrix(ships[1]).TransformBox(e.GetComponent<MapGridComponent>(ships[1]).LocalAABB);
            Assert.That(first.Enlarged(7).Intersects(second), Is.False);
            Assert.That(Vector2.Distance(first.Center, second.Center), Is.LessThan(650));
            var mission = e.GetComponent<SalvageExpeditionComponent>(surface);
            Assert.That(mission.EndTime, Is.EqualTo(deadline), "A new crew must not restart the expedition timer.");
            Assert.That(mission.ReturnLocations.Keys, Is.EquivalentTo(ships));
            Assert.That(mission.ReturnLocations.Values.All(v => v.Coordinates.EntityId == orbit), Is.True);
            mission.Completed = true; mission.ObjectivesCompleted = mission.ObjectivesTotal;
        });
        await Request(0, SectorExpeditionAction.LeavePlanet);
        await Wait(() => e.GetComponent<TransformComponent>(ships[0]).MapUid == orbit && !e.HasComponent<FTLComponent>(ships[0]));
        await server.WaitAssertion(() => Assert.That(e.EntityExists(surface), Is.True, "The second crew must retain its planet."));
        await Request(1, SectorExpeditionAction.LeavePlanet);
        await Wait(() => e.GetComponent<TransformComponent>(ships[1]).MapUid == orbit && !e.HasComponent<FTLComponent>(ships[1]));
        if (!localPlanet) await Wait(() => Complete(tasks, db.ReadVoyageWorldAsync(false, [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Research.GetValueOrDefault("echo")?.Completed.Contains(0) == true);
        await server.WaitAssertion(() =>
        {
            if (localPlanet)
            {
                for (var i = 0; i < 2; i++) Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(players[i].UserId.UserId, accounts[i].Slot)).Bank, Is.EqualTo(accounts[i].Bank));
                Assert.That(Complete(tasks, db.ReadVoyageWorldAsync(false, [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Research.ContainsKey(VoyageRouteModel.Home), Is.False);
                return;
            }
            var total = (6000 + SectorResearchDefinition.Count("echo", voyages.WorldSnapshot.Seed) - 1) / SectorResearchDefinition.Count("echo", voyages.WorldSnapshot.Seed);
            var first = Complete(tasks, db.ReadEconomyAccountAsync(players[0].UserId.UserId, accounts[0].Slot)).Bank - accounts[0].Bank + 6000;
            var second = Complete(tasks, db.ReadEconomyAccountAsync(players[1].UserId.UserId, accounts[1].Slot)).Bank - accounts[1].Bank;
            Assert.That(first + second, Is.EqualTo(total)); Assert.That(Math.Abs(first - second), Is.LessThanOrEqualTo(1));
        });
        if (!localPlanet)
        {
            long[] settled = [];
            EntityUid loot = default, debris = default;
            await server.WaitAssertion(() =>
            {
                Assert.That(e.EntityExists(surface), Is.True);
                Assert.That(e.GetComponent<MetaDataComponent>(surface).EntityPaused, Is.True, "An empty retained surface must sleep.");
                settled = players.Select((p, i) => Complete(tasks, db.ReadEconomyAccountAsync(p.UserId.UserId, accounts[i].Slot)).Bank).ToArray();
                loot = e.SpawnEntity("SheetSteel", new EntityCoordinates(surface, 1000, 1000));
                debris = e.SpawnEntity("SheetSteel", new EntityCoordinates(orbit, 1000, 1000));
                var pos = (Vector2)typeof(SectorExpeditionSystem).GetMethod("PlanetPosition", Private)!.Invoke(sectors, ["echo", 0])!;
                e.System<SharedTransformSystem>().SetCoordinates(ships[0], new EntityCoordinates(orbit, pos));
            });
            await Request(0, SectorExpeditionAction.Land);
            await Wait(() => e.GetComponent<TransformComponent>(ships[0]).MapUid == surface && !e.HasComponent<FTLComponent>(ships[0]));
            await server.WaitAssertion(() =>
            {
                Assert.That(e.EntityExists(loot), Is.True, "Revisiting uses the same surface and original remaining loot.");
                Assert.That(e.GetComponent<MetaDataComponent>(surface).EntityPaused, Is.False);
            });
            await Request(0, SectorExpeditionAction.LeavePlanet);
            await Wait(() => e.GetComponent<TransformComponent>(ships[0]).MapUid == orbit && !e.HasComponent<FTLComponent>(ships[0]));
            await server.WaitAssertion(() =>
            {
                for (var i = 0; i < 2; i++) Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(players[i].UserId.UserId, accounts[i].Slot)).Bank, Is.EqualTo(settled[i]), "Revisiting must not issue another research reward.");
            });
            await Request(0, SectorExpeditionAction.ReturnHome);
            await Wait(() => e.GetComponent<TransformComponent>(ships[0]).MapUid == home.MapUid && !e.HasComponent<FTLComponent>(ships[0]));
            await server.WaitAssertion(() => Assert.That(e.EntityExists(orbit), Is.True, "The second crew still occupies this sector."));
            await Request(1, SectorExpeditionAction.ReturnHome);
            await Wait(() => ships.All(s => e.GetComponent<TransformComponent>(s).MapUid == home.MapUid && !e.HasComponent<FTLComponent>(s)));
            await Wait(() => !e.EntityExists(orbit) && !e.EntityExists(surface));
            await server.WaitAssertion(() => { Assert.That(e.EntityExists(loot), Is.False); Assert.That(e.EntityExists(debris), Is.False); });
        }
        await pair.CleanReturnAsync();
    }
}
