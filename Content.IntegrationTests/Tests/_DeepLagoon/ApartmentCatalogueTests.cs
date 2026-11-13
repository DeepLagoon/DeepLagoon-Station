using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ApartmentShopSystem = Content.Client._DeepLagoon.Apartments.ApartmentShopSystem;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.HTN;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos;
using Content.Shared.Buckle.Components;
using Content.Shared.NPC.Components;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentCatalogueTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static IApartmentLayoutStore Store(ApartmentSystem system) =>
        (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(system)!;
    private static void Show(ApartmentSystem system, ICommonSession session) =>
        typeof(ApartmentSystem).GetMethod("PublishShop", Private)!.Invoke(system, new object[] { session, "" });
    private static void Select(ApartmentSystem system, ICommonSession session, string template)
    {
        var tokens = (Dictionary<NetUserId, Guid>) typeof(ApartmentSystem).GetField("_shopTokens", Private)!.GetValue(system)!;
        typeof(ApartmentSystem).GetMethod("ShopAction", Private)!.Invoke(system,
            new object[] { new ApartmentShopAction(tokens[session.UserId], "select", template), new EntitySessionEventArgs(session) });
    }
    private static ApartmentShopState ClientState(IEntityManager entities) =>
        (ApartmentShopState) typeof(ApartmentShopSystem).GetField("_state", Private)!.GetValue(entities.System<ApartmentShopSystem>())!;

    [Test]
    public async Task AllSixMapsLoadWithTheirPricesAndUsableElevatorCabins()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var entities = pair.Server.EntMan;
            var system = entities.System<ApartmentSystem>();
            var elevator = entities.System<ApartmentElevatorSystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            Assert.That(pair.Server.CfgMan.GetCVar(ApartmentCVars.DefaultTemplate), Is.EqualTo("DLApartmentSmall"));
            typeof(ApartmentSystem).GetMethod("EnsureMaps", Private)!.Invoke(system, null);
            foreach (var (id, price) in new[] { ("DLApartmentSmall", 0), ("DLApartmentNormal", 550),
                ("DLApartmentSilhouette", 1230), ("DLApartmentScrap", 450), ("DLApartmentSnowCabin", 1050), ("DLApartmentStrip", 2100) })
            {
                TestContext.Progress.WriteLine("Loading apartment template: " + id);
                var template = prototypes.Index<ApartmentTemplatePrototype>(id);
                Assert.That(template.Price, Is.EqualTo(price), id);
                Assert.That(template.Available && !template.ExistingOnly, Is.True, id);
                var home = (ApartmentSystem.Instance) typeof(ApartmentSystem).GetMethod("CreateFresh", Private)!
                    .Invoke(system, new object[] { new NetUserId(Guid.NewGuid()), id, true })!;
                var panel = (EntityUid) typeof(ApartmentElevatorSystem).GetMethod("PanelOn", Private)!.Invoke(elevator, new object[] { home.Grid })!;
                var body = entities.SpawnEntity("MobHuman", new EntityCoordinates(home.Grid, new Vector2(template.Arrival.X + .5f, template.Arrival.Y + .5f)));
                Assert.That(typeof(ApartmentElevatorSystem).GetMethod("Inside", Private)!.Invoke(elevator, new object[] { body, panel }), Is.True, id);
                Assert.That(typeof(ApartmentElevatorSystem).GetMethod("CabinDoors", Private)!.Invoke(elevator, new object[] { panel }), Is.Not.Null, id);
                entities.DeleteEntity(body);
                entities.DeleteEntity(home.Grid);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BitrunRewardApartmentLoadsAndRequiresAGrant()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var entities = pair.Server.EntMan;
            var system = entities.System<ApartmentSystem>();
            var elevator = entities.System<ApartmentElevatorSystem>();
            var owner = new NetUserId(Guid.NewGuid());
            const string id = "DLApartmentBitrun";
            var template = pair.Server.ResolveDependency<IPrototypeManager>().Index<ApartmentTemplatePrototype>(id);
            var canSelect = typeof(ApartmentSystem).GetMethod("CanSelectTemplate", Private)!;
            Assert.That(canSelect.Invoke(system, new object[] { owner, template }), Is.False);
            Assert.That(system.GrantTemplate(owner, id, out var owned), Is.Null);
            Assert.That(owned, Is.False);
            Assert.That(canSelect.Invoke(system, new object[] { owner, template }), Is.True);
            Assert.That(system.GrantTemplate(owner, id, out owned), Is.Null);
            Assert.That(owned, Is.True);

            var home = (ApartmentSystem.Instance) typeof(ApartmentSystem).GetMethod("CreateFresh", Private)!
                .Invoke(system, new object[] { owner, id, false })!;
            AssertBitrunEnvironment(entities, home.Grid, 21);
            var panel = (EntityUid) typeof(ApartmentElevatorSystem).GetMethod("PanelOn", Private)!.Invoke(elevator, new object[] { home.Grid })!;
            var arrival = new EntityCoordinates(home.Grid, new Vector2(template.Arrival.X + .5f, template.Arrival.Y + .5f));
            var body = entities.SpawnEntity("MobHuman", arrival);
            Assert.That(typeof(ApartmentElevatorSystem).GetMethod("Inside", Private)!.Invoke(elevator, new object[] { body, panel }), Is.True);
            Assert.That(typeof(ApartmentElevatorSystem).GetMethod("CabinDoors", Private)!.Invoke(elevator, new object[] { panel }), Is.Not.Null);

            var windows = 0;
            var beds = 0;
            var hierophants = 0;
            var innerWalls = 0;
            var outerWalls = 0;
            var children = entities.GetComponent<TransformComponent>(home.Grid).ChildEnumerator;
            while (children.MoveNext(out var uid))
            {
                switch (entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID)
                {
                    case "ReinforcedWindow":
                    case "ReinforcedWindowDiagonal":
                        Assert.That(entities.GetComponent<TransformComponent>(uid).Anchored, Is.True);
                        windows++;
                        break;
                    case "DLApartmentBitrunInteriorWall":
                        Assert.That(entities.GetComponent<OccluderComponent>(uid).Enabled, Is.False);
                        innerWalls++;
                        break;
                    case "BitrunVirtualWall":
                        Assert.That(entities.GetComponent<OccluderComponent>(uid).Enabled, Is.True);
                        outerWalls++;
                        break;
                    case "DLApartmentBitrunBed":
                        Assert.That(entities.HasComponent<StrapComponent>(uid), Is.True);
                        Assert.That(entities.HasComponent<BitrunPodComponent>(uid), Is.False);
                        beds++;
                        break;
                    case "DLApartmentBitrunHierophant":
                        Assert.That(entities.HasComponent<GunComponent>(uid), Is.False);
                        Assert.That(entities.HasComponent<MeleeWeaponComponent>(uid), Is.False);
                        Assert.That(entities.HasComponent<HTNComponent>(uid), Is.False);
                        Assert.That(entities.GetComponent<NpcFactionMemberComponent>(uid).Factions.Select(f => f.Id), Is.EqualTo(new[] { "Passive" }));
                        hierophants++;
                        break;
                }
            }
            Assert.That(windows, Is.EqualTo(28));
            Assert.That(beds, Is.EqualTo(2));
            Assert.That(hierophants, Is.EqualTo(2));
            Assert.That(innerWalls, Is.EqualTo(62));
            Assert.That(outerWalls, Is.EqualTo(59));
            entities.DeleteEntity(body);
            entities.DeleteEntity(home.Grid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BitrunBarriersAndAirSurviveManualLoadAndLegacySnapshotRestore()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        EntityUid manual = default, restored = default;
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId, runMapInit: true);
            var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TryLoadGrid(mapId, new ResPath("/Maps/_DeepLagoon/Apartments/bitrun.yml"), out var loaded), Is.True);
            manual = loaded!.Value.Owner;
            Assert.That(entities.HasComponent<ApartmentGridComponent>(manual), Is.False);
            AssertBitrunEnvironment(entities, manual, 21);

            var apartments = entities.System<ApartmentSystem>();
            var owner = new NetUserId(Guid.NewGuid());
            Assert.That(apartments.GrantTemplate(owner, "DLApartmentBitrun", out _), Is.Null);
            var store = Store(apartments);
            var template = server.ResolveDependency<IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBitrun");
            store.Select(owner.UserId, template);
            var legacy = (ApartmentSystem.Instance) typeof(ApartmentSystem).GetMethod("CreateFresh", Private)!
                .Invoke(apartments, [owner, template.ID, false])!;
            legacy.Delta.OxygenPercent = 35;
            entities.GetComponent<GridEnvironmentComponent>(legacy.Grid).SafeAtmosphere = false;
            var barriers = BitrunBarriers(entities, legacy.Grid);
            var coords = entities.GetComponent<TransformComponent>(barriers[0]).Coordinates;
            entities.DeleteEntity(barriers[0]);
            entities.SpawnEntity("WallForce", coords);
            entities.DeleteEntity(barriers[1]);
            Assert.That(apartments.SaveApartment(legacy), Is.True);
            entities.DeleteEntity(legacy.Grid);

            var home = apartments.PrepareElevatorApartment(owner);
            restored = home.Grid;
            AssertBitrunEnvironment(entities, restored, 35);
            Assert.That(home.MapFurniture.Values.Any(furniture => furniture.Prototype == "DLApartmentBitrunBarrier"), Is.False);
        });

        await server.WaitRunTicks(13 * server.Timing.TickRate);
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            AssertBitrunEnvironment(entities, manual, 21);
            AssertBitrunEnvironment(entities, restored, 35);
        });
        await pair.CleanReturnAsync();
    }

    private static EntityUid[] BitrunBarriers(IEntityManager entities, EntityUid grid)
    {
        var barriers = new List<EntityUid>();
        var children = entities.GetComponent<TransformComponent>(grid).ChildEnumerator;
        while (children.MoveNext(out var uid))
        {
            var prototype = entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID;
            Assert.That(prototype, Is.Not.EqualTo("WallForce"));
            if (prototype == "DLApartmentBitrunBarrier")
                barriers.Add(uid);
        }
        return barriers.ToArray();
    }

    private static void AssertBitrunEnvironment(IEntityManager entities, EntityUid grid, int oxygenPercent)
    {
        var barriers = BitrunBarriers(entities, grid);
        Assert.That(barriers, Has.Length.EqualTo(8));
        foreach (var barrier in barriers)
        {
            Assert.That(entities.HasComponent<TimedDespawnComponent>(barrier), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(barrier).Anchored, Is.True);
        }

        var atmosphere = entities.System<AtmosphereSystem>();
        var map = entities.GetComponent<TransformComponent>(grid).MapUid;
        foreach (var position in new[] { new Vector2i(8, 12), new Vector2i(9, 5), new Vector2i(8, 2) })
        {
            var air = atmosphere.GetTileMixture(grid, map, position)!;
            Assert.That(air.Pressure, Is.EqualTo(Atmospherics.OneAtmosphere).Within(.01f));
            Assert.That(air.GetMoles(Gas.Oxygen) / air.TotalMoles * 100f, Is.EqualTo(oxygenPercent).Within(.01f));
        }
        var outside = new Vector2i(-1, 5);
        Assert.That(atmosphere.GetTileMixture(grid, map, outside)!.Pressure,
            Is.EqualTo(atmosphere.GetTileMixture(null, map, outside)!.Pressure).Within(.01f));
    }

    [Test]
    public async Task RetiredBasicIsHiddenAndCannotBeSelectedOrGrantedToNewOwners()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            Assert.That(system.GrantTemplate(session.UserId, "DLApartmentBasic", out var owned), Is.EqualTo("apartment_unavailable"));
            Assert.That(owned, Is.False);
            Show(system, session);
            Select(system, session, "DLApartmentBasic");
            Assert.That(Store(system).Owned(session.UserId.UserId, "DLApartmentBasic"), Is.False);
            Assert.That(Store(system).HasLayout(session.UserId.UserId, "DLApartmentBasic"), Is.False);
            Assert.That(system.PrepareElevatorApartment(session.UserId).Template.ID, Is.EqualTo("DLApartmentSmall"));
        });
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var state = ClientState(pair.Client.EntMan);
            Assert.That(state.Templates.Any(p => p.Id == "DLApartmentBasic"), Is.False);
            Assert.That(state.Templates.Single(p => p.Id == "DLApartmentSmall").Owned, Is.True);
            typeof(ApartmentShopSystem).GetMethod("Close", Private)!.Invoke(pair.Client.EntMan.System<ApartmentShopSystem>(), null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExistingBasicLayoutRemainsSelectedAndCanBeRestoredAfterSwitching()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            var basic = pair.Server.ResolveDependency<IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic");
            var saved = Store(system).Select(session.UserId.UserId, basic);
            saved.OxygenPercent = 35;
            saved.Revision++;
            Assert.That(Store(system).Save(session.UserId.UserId, saved, saved.Revision - 1), Is.True);
            Assert.That(Store(system).Owned(session.UserId.UserId, basic.ID), Is.False, "Old free apartments can have saves without purchase receipts");
            Assert.That(system.PrepareElevatorApartment(session.UserId).Template.ID, Is.EqualTo(basic.ID));
            Show(system, session);
        });
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var state = ClientState(pair.Client.EntMan);
            var basic = state.Templates.Single(p => p.Id == "DLApartmentBasic");
            Assert.That(basic.Owned && basic.Selected, Is.True);
        });
        await pair.Server.WaitAssertion(() => Select(pair.Server.EntMan.System<ApartmentSystem>(), pair.Server.PlayerMan.Sessions.Single(), "DLApartmentSmall"));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            Assert.That(Store(system).Load(session.UserId.UserId)!.Template, Is.EqualTo("DLApartmentSmall"));
            Assert.That(Store(system).HasLayout(session.UserId.UserId, "DLApartmentBasic"), Is.True);
            Show(system, session);
            Select(system, session, "DLApartmentBasic");
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var owner = pair.Server.PlayerMan.Sessions.Single().UserId;
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            Assert.That(Store(system).Load(owner.UserId)!.Template, Is.EqualTo("DLApartmentBasic"));
            Assert.That(Store(system).Load(owner.UserId)!.OxygenPercent, Is.EqualTo(35));
            Assert.That(system.PrepareElevatorApartment(owner).Template.ID, Is.EqualTo("DLApartmentBasic"));
        });
        await pair.Client.WaitAssertion(() => typeof(ApartmentShopSystem).GetMethod("Close", Private)!.Invoke(pair.Client.EntMan.System<ApartmentShopSystem>(), null));
        await pair.CleanReturnAsync();
    }
}
