using System.Linq;
using System.Collections.Generic;
using Content.Client._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Inventory;
using Content.Shared.GameTicking;
using Content.Shared.Maps;
using Content.Shared.Preferences;
using Content.Shared.Light.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Components;
using Content.Shared.Atmos;
using Content.Shared.Verbs;
using Robust.Shared.Localization;
using Content.Shared._CE.ZLevels.Core.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Client.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentIntegrationTests
{
    [Test]
    public async Task ThirtyApartmentsShareMapWithoutOverlappingAndKeepPositionsWhenWoken()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var testMap = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentStudio");
            Assert.That(config.GetCVar(ApartmentCVars.LoadedLimit), Is.EqualTo(30));
            var session = pair.Server.PlayerMan.Sessions.Single();
            var body = entities.SpawnEntity("MobHuman", testMap.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            var system = entities.System<ApartmentSystem>();
            EntityUid? residentialMap = null;
            var homes = new List<ApartmentSystem.Instance>();
            for (var index = 0; index < 30; index++)
            {
                var owner = new NetUserId(Guid.NewGuid());
                system.EnsureGuestPolicy(owner);
                Assert.That(system.Invite(owner, session.UserId), Is.True);
                Assert.That(system.Enter(session, owner, out var message), Is.True, message);
                var home = system.Instances[owner];
                var transform = entities.GetComponent<TransformComponent>(home.Grid);
                residentialMap ??= transform.MapUid;
                Assert.That(transform.MapUid, Is.EqualTo(residentialMap));
                foreach (var other in homes)
                {
                    var separate = home.Position.X >= other.Position.X + other.Template.Width + 2 ||
                        other.Position.X >= home.Position.X + home.Template.Width + 2 ||
                        home.Position.Y >= other.Position.Y + other.Template.Height + 2 ||
                        other.Position.Y >= home.Position.Y + home.Template.Height + 2;
                    Assert.That(separate, Is.True, "Apartment bounds must have a two-tile gap.");
                }
                homes.Add(home);
                Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            }
            Assert.That(system.Instances, Has.Count.EqualTo(30));
            Assert.That(homes[1].Position.X, Is.EqualTo(homes[0].Template.Width + 2));
            Assert.That(homes[1].Position.Y, Is.EqualTo(0));
            Assert.That(system.Enter(session, session.UserId, out _), Is.False, "The 31st apartment must exceed the limit.");
            foreach (var home in homes)
            {
                Assert.That(home.Frozen, Is.True);
                Assert.That(system.Enter(session, home.Owner, out var message), Is.True, message);
                var transform = entities.GetComponent<TransformComponent>(home.Grid);
                Assert.That(transform.MapUid, Is.EqualTo(residentialMap));
                Assert.That(transform.LocalPosition, Is.EqualTo(home.Position));
                Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            }
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("DLApartmentBasic")]
    public async Task LatejoinSpawnsInOwnApartmentOnlyForEnabledStation(string template)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var testMap = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = entities.System<ApartmentSystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var stationConfig = prototypes.Index<GameMapPrototype>("Frontier").Stations["Frontier"];
            var station = entities.SpawnEntity(stationConfig.StationPrototype, MapCoordinates.Nullspace,
                stationConfig.StationComponentOverrides);
            var otherStation = entities.SpawnEntity("StandardFrontierOutpost", MapCoordinates.Nullspace);
            Assert.That(entities.HasComponent<ApartmentLatejoinSpawnComponent>(station), Is.True);
            Assert.That(entities.HasComponent<ApartmentLatejoinSpawnComponent>(otherStation), Is.False);
            var body = entities.SpawnEntity("MobHuman", testMap.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            var original = entities.GetComponent<TransformComponent>(body).Coordinates;
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitColorBlue", original);
            var inventory = entities.System<InventorySystem>();
            Assert.That(inventory.TryEquip(body, uniform, "jumpsuit", force: true), Is.True);

            void SpawnComplete(bool lateJoin, EntityUid? selectedStation = null) => entities.EventBus.RaiseLocalEvent(body,
                new PlayerSpawnCompleteEvent(body, session, null, lateJoin, false, 1,
                    selectedStation ?? station, HumanoidCharacterProfile.DefaultWithSpecies()), true);

            config.SetCVar(ApartmentCVars.Enabled, false);
            SpawnComplete(true);
            Assert.That(system.TryGetVisit(session.UserId, out _, out _), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(original));

            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, template);
            SpawnComplete(false);
            Assert.That(system.TryGetVisit(session.UserId, out _, out _), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(original));
            Assert.That(system.Instances.ContainsKey(session.UserId), Is.False);
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var roundStartUniform), Is.True);
            Assert.That(roundStartUniform, Is.EqualTo(uniform));
            Assert.That(entities.GetComponent<MetaDataComponent>(uniform).EntityPaused, Is.False);

            SpawnComplete(true, otherStation);
            Assert.That(system.TryGetVisit(session.UserId, out _, out _), Is.False);
            Assert.That(system.Instances.ContainsKey(session.UserId), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(original));
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var otherStationUniform), Is.True);
            Assert.That(otherStationUniform, Is.EqualTo(uniform));
            Assert.That(entities.GetComponent<MetaDataComponent>(uniform).EntityPaused, Is.False);

            SpawnComplete(true);
            Assert.That(system.TryGetVisit(session.UserId, out var home, out var resident), Is.True);
            Assert.That(home.Owner, Is.EqualTo(session.UserId));
            Assert.That(resident, Is.EqualTo(body));
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(home.Grid));
            var beds = new List<EntityUid>();
            var query = entities.EntityQueryEnumerator<TransformComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out var transform, out var metadata))
                if (transform.ParentUid == home.Grid && metadata.EntityPrototype is { } prototype &&
                    prototypes.EnumerateParents(prototype, includeSelf: true).Any(parent => parent.ID == "Bed"))
                    beds.Add(uid);
            Assert.That(beds, Is.Not.Empty);
            var spawn = entities.GetComponent<TransformComponent>(body).Coordinates;
            Assert.That(beds.Any(bed => entities.GetComponent<TransformComponent>(bed).Coordinates == spawn), Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(uniform).EntityPaused, Is.True);

            Assert.That(system.Leave(session.UserId, out var message), Is.True, message);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(original));
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var restored), Is.True);
            Assert.That(restored, Is.EqualTo(uniform));
            Assert.That(system.Enter(session, session.UserId, out message), Is.True, message);
            var arrival = new EntityCoordinates(home.Grid, home.Template.Arrival.X + .5f, home.Template.Arrival.Y + .5f);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(arrival));
            foreach (var bed in beds) entities.DeleteEntity(bed);
            Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            SpawnComplete(true);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(arrival));
            Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(original));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PreviewApplyEscrowPersistenceAndFreezeRunThroughConnectedClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var testMap = await pair.CreateTestMap();
        EntityUid body = default;
        EntityUid originalUniform = default;
        EntityUid grid = default;
        NetEntity originalSofa = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentStudio");
            body = entities.SpawnEntity("MobHuman", testMap.GridCoords);
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            originalUniform = entities.SpawnEntity("ClothingUniformJumpsuitColorBlue", testMap.GridCoords);
            Assert.That(entities.System<InventorySystem>().TryEquip(body, originalUniform, "jumpsuit", force: true), Is.True);
            var system = entities.System<ApartmentSystem>();
            Assert.That(system.Enter(session, session.UserId, out var message), Is.True, message);
            Assert.That(entities.GetComponent<TransformComponent>(originalUniform).MapID, Is.Not.EqualTo(entities.GetComponent<TransformComponent>(body).MapID));
            Assert.That(entities.GetComponent<MetaDataComponent>(originalUniform).EntityPaused, Is.True);
            grid = system.Instances[session.UserId].Grid;
            originalSofa = entities.GetNetEntity(system.Instances[session.UserId].Furniture["base_sofa_01"]);
            var terminals = entities.EntityQueryEnumerator<ApartmentTerminalComponent, TransformComponent>();
            EntityUid terminal = default;
            while (terminals.MoveNext(out var uid, out _, out var transform))
                if (transform.GridUid == grid) { terminal = uid; break; }
            Assert.That(terminal, Is.Not.EqualTo(default(EntityUid)));
            var verbs = entities.System<Content.Server.Verbs.VerbSystem>()
                .GetLocalVerbs(terminal, body, typeof(AlternativeVerb), force: true).Select(verb => verb.Text).ToArray();
            Assert.That(verbs, Does.Contain(Loc.GetString("dl-apartment-exit")));
            Assert.That(verbs, Does.Contain(Loc.GetString("dl-apartment-edit")));
            Assert.That(verbs, Does.Not.Contain(Loc.GetString("dl-apartment-travel-menu")));
        });
        await pair.RunTicksSync(35);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.HasComponent<GridAtmosphereComponent>(grid), Is.False);
            var air = entities.System<AtmosphereSystem>().GetContainingMixture(body);
            Assert.That(air, Is.Not.Null);
            Assert.That(air!.Immutable, Is.True);
            Assert.That(air.GetMoles(Gas.Oxygen), Is.GreaterThan(20));
            Assert.That(air.Pressure, Is.InRange(90, 110));
        });
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<ApartmentSystem>().OpenEditor(pair.Server.PlayerMan.Sessions.Single()), Is.True));
        await pair.RunTicksSync(35);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var editor = entities.System<ApartmentEditorSystem>();
            var clientBody = pair.Client.PlayerMan.LocalEntity!.Value;
            Assert.That(entities.HasComponent<CEZLevelViewerComponent>(clientBody), Is.True);
            var clientMap = entities.GetComponent<TransformComponent>(clientBody).MapUid;
            Assert.That(clientMap, Is.Not.Null);
            Assert.That(entities.GetComponent<MapLightComponent>(clientMap!.Value).AmbientLightColor, Is.EqualTo(Color.White));
            Assert.That(entities.GetComponent<ImplicitRoofComponent>(entities.GetComponent<TransformComponent>(clientBody).GridUid!.Value).Color, Is.EqualTo(Color.White));
            Assert.That(editor.IsOpen, Is.True);
            Assert.That(editor.Draft, Has.Count.EqualTo(6));
            Assert.That(entities.GetComponent<SpriteComponent>(entities.GetEntity(originalSofa)).Visible, Is.False);
            // Client draft is isolated until an explicit network apply.
            editor.Draft.Single(p => p.Id == "base_sofa_01").X = 3;
        });
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<ApartmentSystem>()
            .Instances[pair.Server.PlayerMan.Sessions.Single().UserId].Delta.Changed, Is.Empty));
        // Exercise the same panel action as the Apply button, including its sandbox parser.
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var editor = entities.System<ApartmentEditorSystem>();
            var ui = pair.Client.ResolveDependency<Robust.Client.UserInterface.IUserInterfaceManager>();
            var window = ui.WindowRoot.Children.OfType<ApartmentEditorWindow>().Single();
            // Panel actions route through a private handler; invoke it with the actual bounded JSON payload.
            var method = typeof(ApartmentEditorSystem).GetMethod("OnAction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var layout = editor.Draft.Select(p => new { id = p.Id, furniture = p.Furniture, x = p.X, y = p.Y, rotation = p.Rotation });
            method.Invoke(editor, new object[] { "apply", System.Text.Json.JsonSerializer.Serialize(new { layout }) });
        });
        await pair.RunTicksSync(35);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = entities.System<ApartmentSystem>();
            var instance = system.Instances[session.UserId];
            Assert.That(instance.Delta.Changed, Has.Count.EqualTo(1));
            Assert.That(instance.Delta.Changed[0].X, Is.EqualTo(3));
            Assert.That(instance.Delta.Added, Is.Empty);
            var sofa = instance.Furniture["base_sofa_01"];
            Assert.That(entities.GetComponent<TransformComponent>(sofa).LocalPosition.X, Is.EqualTo(3.5f));
            // Reject a second request from the old draft revision before mutating anything.
            Assert.That(system.Apply(session, Guid.Empty, 0, new List<ApartmentPlacement>()), Does.Contain("доступа"));
            // Losing the attached participant must freeze the entire room even
            // while their body and deposited inventory remain in the instance.
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            system.RefreshActivity(instance);
            Assert.That(instance.Frozen, Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(body).EntityPaused, Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(sofa).EntityPaused, Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            system.RefreshActivity(instance);
            Assert.That(instance.Frozen, Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(body).EntityPaused, Is.False);
            Assert.That(system.Leave(session.UserId, out var message), Is.True, message);
            Assert.That(entities.System<InventorySystem>().TryGetSlotEntity(body, "jumpsuit", out var restored), Is.True);
            Assert.That(restored, Is.EqualTo(originalUniform));
            Assert.That(entities.GetComponent<MetaDataComponent>(originalUniform).EntityPaused, Is.False);
            Assert.That(instance.Frozen, Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(grid).EntityPaused, Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(sofa).EntityPaused, Is.True);
            Assert.That(system.Enter(session, session.UserId, out message), Is.True, message);
            Assert.That(instance.Frozen, Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(grid).EntityPaused, Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(sofa).EntityPaused, Is.False);
            Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() => Assert.That(pair.Client.EntMan.System<ApartmentEditorSystem>().IsOpen, Is.False));
        await pair.CleanReturnAsync();
    }
}
