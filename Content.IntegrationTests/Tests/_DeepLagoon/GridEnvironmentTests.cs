using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Mapping;
using Content.Server._NF.PacifiedZone;
using Content.Server.Administration.Managers;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Tiles;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GridEnvironmentTests
{
    [TestPrototypes]
    private const string ConsumerPrototype = @"
- type: entity
  id: GridEnvironmentConsumerProbe
  components:
  - type: Transform
    anchored: true
  - type: NodeContainer
    nodes:
      input:
        !type:CableDeviceNode
        nodeGroupID: HVPower
  - type: PowerConsumer
";

    [Test]
    public async Task BasicApartmentHasImmutableAirPowerGravityAndProtectionWithoutInfrastructure()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        EntityUid grid = default, microwave = default, consumer = default, body = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentBasic");
            // This geometry fixture deliberately keeps the legacy map as a free test template.
            pair.Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic").ExistingOnly = false;
            var session = pair.Server.PlayerMan.Sessions.Single();
            body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            var apartments = entities.System<ApartmentSystem>();
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];
            Assert.That(home.Template.ID, Is.EqualTo("DLApartmentBasic"));
            grid = home.Grid;
            var terminals = entities.EntityQueryEnumerator<ApartmentTerminalComponent, TransformComponent>();
            var terminalCount = 0;
            while (terminals.MoveNext(out _, out _, out var terminalTransform))
                if (terminalTransform.GridUid == grid)
                {
                    terminalCount++;
                    Assert.That(terminalTransform.LocalPosition, Is.EqualTo(new Vector2(8.5f, 1.5f)));
                }
            Assert.That(terminalCount, Is.EqualTo(1));
            var map = entities.System<SharedMapSystem>();
            var tiles = map.GetAllTiles(grid, entities.GetComponent<MapGridComponent>(grid)).ToArray();
            Assert.That(tiles.Length, Is.EqualTo(129));
            var atmos = entities.System<AtmosphereSystem>();
            foreach (var tile in tiles)
            {
                var air = atmos.GetTileMixture(grid, entities.GetComponent<TransformComponent>(grid).MapUid, tile.GridIndices);
                Assert.That(air, Is.Not.Null);
                Assert.That(air!.Immutable, Is.True);
                Assert.That(air.Pressure, Is.InRange(100, 103));
                Assert.That(air.Temperature, Is.EqualTo(293.15f).Within(.01));
                air.AdjustMoles(Gas.Plasma, 100);
                air.Temperature = 1000;
                Assert.That(air.GetMoles(Gas.Plasma), Is.Zero);
                Assert.That(air.Temperature, Is.EqualTo(293.15f).Within(.01));
            }
            Assert.That(entities.HasComponent<ApartmentProtectedGridComponent>(grid), Is.True);
            var protection = entities.GetComponent<ProtectedGridComponent>(grid);
            Assert.That(protection.PreventFloorRemoval && protection.PreventRCDUse && protection.PreventExplosions && protection.PreventEmpEvents, Is.True);
            var zone = entities.GetComponent<PacifiedZoneGeneratorComponent>(grid);
            Assert.That(zone.WholeGrid && zone.DisallowAllCombat, Is.True);
            var gravity = entities.GetComponent<GravityComponent>(grid);
            Assert.That(gravity.Enabled && gravity.Inherent, Is.True);
            consumer = entities.SpawnEntity("GridEnvironmentConsumerProbe", new EntityCoordinates(grid, new Vector2(4.5f, 8.5f)));
            entities.GetComponent<PowerConsumerComponent>(consumer).DrawRate = 456;
            microwave = entities.SpawnEntity("KitchenMicrowave", new EntityCoordinates(grid, new Vector2(4.5f, 7.5f)));
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var receiver = entities.GetComponent<ApcPowerReceiverComponent>(microwave);
            Assert.That(receiver.NeedsPower, Is.False);
            Assert.That(receiver.Powered, Is.True);
            Assert.That(entities.GetComponent<PowerConsumerComponent>(consumer).ReceivedPower, Is.EqualTo(456));
            var damageable = entities.GetComponent<DamageableComponent>(microwave);
            var before = damageable.TotalDamage;
            entities.System<DamageableSystem>().TryChangeDamage(microwave, new DamageSpecifier { DamageDict = new() { ["Blunt"] = 1000 } });
            Assert.That(damageable.TotalDamage, Is.EqualTo(before));
            var transform = entities.System<SharedTransformSystem>();
            transform.SetCoordinates(microwave, outside.GridCoords);
            transform.SetCoordinates(consumer, outside.GridCoords);
            var map = entities.System<SharedMapSystem>();
            var vacuum = map.CreateMap(out var vacuumId);
            transform.SetCoordinates(grid, new EntityCoordinates(vacuum, Vector2.Zero));
            var air = entities.System<AtmosphereSystem>().GetContainingMixture(body);
            Assert.That(air!.Immutable, Is.True);
            Assert.That(air.Pressure, Is.InRange(100, 103));
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<ApcPowerReceiverComponent>(microwave).NeedsPower, Is.True);
            Assert.That(entities.GetComponent<ApcPowerReceiverComponent>(microwave).Powered, Is.False);
            Assert.That(entities.GetComponent<PowerConsumerComponent>(consumer).PowerBypass, Is.False);
            Assert.That(entities.GetComponent<PowerConsumerComponent>(consumer).ReceivedPower, Is.Zero);
            var session = pair.Server.PlayerMan.Sessions.Single();
            entities.System<ApartmentSystem>().Leave(session.UserId, out _);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EditorEnvironmentSettingsAreAuthorizedReversibleAndSurviveMapSerialization()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var testMap = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var player = pair.Server.PlayerMan.Sessions.Single();
            var editor = entities.System<GridEditorSystem>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var method = typeof(GridEditorSystem).GetMethod("Environment", flags)!;
            void Request(Guid token, string setting, bool enabled) => method.Invoke(editor,
                new object[] { new GridEditorEnvironmentRequest(token, setting, enabled), new EntitySessionEventArgs(player) });
            Request(Guid.NewGuid(), "power", true);
            Assert.That(entities.HasComponent<GridEnvironmentComponent>(testMap.Grid), Is.False);
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            Assert.That(editor.Open(player, testMap.Grid), Does.Contain("открыт"));
            var drafts = (IDictionary)typeof(GridEditorSystem).GetField("_drafts", flags)!.GetValue(editor)!;
            var draft = drafts[player]!;
            var token = (Guid)draft.GetType().GetField("Token")!.GetValue(draft)!;
            Request(Guid.NewGuid(), "gravity", true);
            Assert.That(entities.HasComponent<GridEnvironmentComponent>(testMap.Grid), Is.False);
            var microwave = entities.SpawnEntity("KitchenMicrowave", testMap.GridCoords);
            Request(token, "power", true);
            Request(token, "gravity", true);
            Assert.That(entities.GetComponent<ApcPowerReceiverComponent>(microwave).NeedsPower, Is.False);
            Assert.That(entities.GetComponent<GravityComponent>(testMap.Grid).Enabled, Is.True);
            using var saved = new System.IO.StringWriter();
            var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TrySaveGrid(testMap.Grid, saved), Is.True);
            using var reader = new System.IO.StringReader(saved.ToString());
            Assert.That(loader.TryLoadGeneric(reader, "autonomous-grid", out var restored), Is.True);
            var reloaded = restored!.Grids.Single();
            var environment = entities.GetComponent<GridEnvironmentComponent>(reloaded);
            Assert.That(environment.AlwaysPowered && environment.ForceGravity, Is.True);
            environment.AlwaysPowered = environment.ForceGravity = false;
            entities.System<GridEnvironmentSystem>().RefreshGrid(reloaded, environment);
            Assert.That(entities.GetComponent<GravityComponent>(reloaded).Inherent, Is.False);
            var devices = entities.EntityQueryEnumerator<ApcPowerReceiverComponent, TransformComponent>();
            while (devices.MoveNext(out _, out var receiver, out var transform))
                if (transform.GridUid == reloaded.Owner) Assert.That(receiver.NeedsPower, Is.True);
            Request(token, "power", false);
            Request(token, "gravity", false);
            Assert.That(entities.GetComponent<ApcPowerReceiverComponent>(microwave).NeedsPower, Is.True);
            Assert.That(entities.GetComponent<GravityComponent>(testMap.Grid).Inherent, Is.False);
            var priorZone = entities.EnsureComponent<PacifiedZoneGeneratorComponent>(testMap.Grid);
            priorZone.ImmuneRoles.Add("Captain");
            priorZone.ImmunePlaytime = TimeSpan.Zero;
            entities.AddComponent<ApartmentGridComponent>(testMap.Grid);
            Assert.That(entities.HasComponent<ApartmentProtectedGridComponent>(testMap.Grid), Is.True);
            Assert.That(entities.GetComponent<PacifiedZoneGeneratorComponent>(testMap.Grid).WholeGrid, Is.True);
            Assert.That(priorZone.ImmuneRoles, Is.Empty);
            Assert.That(priorZone.ImmunePlaytime, Is.Null);
            Assert.That(editor.Open(player, testMap.Grid), Does.Contain("открыт"));
            var protectedDraft = drafts[player]!;
            var protectedToken = (Guid)protectedDraft.GetType().GetField("Token")!.GetValue(protectedDraft)!;
            typeof(GridEditorSystem).GetMethod("Apply", flags)!.Invoke(editor, new object[]
            {
                new GridEditorApplyEvent(protectedToken, new List<GridEditorItem>
                {
                    new() { Id = "new_apartment_panel", Kind = "entity", Prototype = "DLApartmentElevatorPanel",
                        X = .5f, Y = .5f, Anchored = true }
                }, patch: true),
                new EntitySessionEventArgs(player)
            });
            var panels = entities.EntityQueryEnumerator<ApartmentElevatorComponent, TransformComponent>();
            var foundPanel = false;
            while (panels.MoveNext(out _, out _, out var panelTransform))
                if (panelTransform.GridUid == testMap.Grid.Owner)
                {
                    foundPanel = true;
                    Assert.That(panelTransform.Anchored, Is.True);
                    Assert.That(panelTransform.ParentUid, Is.EqualTo(testMap.Grid.Owner));
                }
            Assert.That(foundPanel, Is.True);
            Assert.That(entities.HasComponent<ApartmentProtectedGridComponent>(testMap.Grid), Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
