#nullable enable annotations
using System;
using System.Linq;
using System.Numerics;
using Content.Server.Mind;
using Content.Shared._CorvaxNext.Silicons.Borgs.Components;
using Content.Shared._FarHorizons.Power.Generation.FissionGenerator;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Construction.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Research.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MachineryMinorFixTests
{
    [TestCase("NuclearReactorSmallFlatpackPurchase", "NuclearReactorSmall", 750000)]
    [TestCase("TurbineSmallFlatpackPurchase", "TurbineSmall", 250000)]
    public async Task CargoFissionKitsUnpackWithMultitoolIntoInitializedMachines(string product, string machine, int price)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            for (var x = -3; x <= 3; x++)
                for (var y = -3; y <= 3; y++)
                    maps.SetTile(map.Grid, new Vector2i(x, y), map.Tile.Tile);
            var cargo = pair.Server.ResolveDependency<IPrototypeManager>().Index<CargoProductPrototype>(product);
            Assert.That(cargo.Cost, Is.EqualTo(price));
            var pack = entities.SpawnEntity(cargo.Product, map.GridCoords);
            var user = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(-3, 0)));
            var wrench = entities.SpawnEntity("Wrench", map.GridCoords);
            var wrong = new InteractUsingEvent(user, wrench, pack, map.GridCoords);
            entities.EventBus.RaiseLocalEvent(pack, wrong);
            Assert.That(wrong.Handled, Is.False);
            var tool = entities.SpawnEntity("Multitool", map.GridCoords);
            var use = new InteractUsingEvent(user, tool, pack, map.GridCoords);
            entities.EventBus.RaiseLocalEvent(pack, use);
            Assert.That(use.Handled, Is.True);
            var built = entities.EntityQuery<MetaDataComponent>().Single(meta => meta.EntityPrototype?.ID == machine).Owner;
            Assert.That(entities.GetComponent<TransformComponent>(built).GridUid, Is.EqualTo(map.Grid.Owner));
            if (machine == "NuclearReactorSmall")
            {
                var reactor = entities.GetComponent<NuclearReactorComponent>(built);
                Assert.That(reactor.ComponentGrid.GetLength(0), Is.EqualTo(5));
                Assert.That(reactor.ComponentGrid.GetLength(1), Is.EqualTo(5));
                Assert.That(reactor.ComponentGrid.Cast<ReactorPartComponent?>().Any(part => part != null), Is.True);
                Assert.That(reactor.Melted, Is.False);
            }
            else
                Assert.That(entities.GetComponent<TurbineComponent>(built).Ruined, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SevastopolResearchSourceConnectsToItsBoltedServerWithoutChangingOrdinaryServers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        // A loaded POI has floor and all its machines before MapInit selects the research server.
        var map = await pair.CreateTestMap(initialized: false);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.SetTile(map.Grid, new Vector2i(1, 0), map.Tile.Tile);
            maps.SetTile(map.Grid, new Vector2i(0, 1), map.Tile.Tile);
            var fixedServer = entities.SpawnEntity("ResearchAndDevelopmentServerSevastopol", map.GridCoords);
            var farm = entities.SpawnEntity("DatafarmResearchSevastopol", map.GridCoords.Offset(Vector2.UnitX));
            foreach (var machine in new[] { fixedServer, farm })
            {
                var transform = entities.GetComponent<TransformComponent>(machine);
                Assert.That(transform.GridUid, Is.EqualTo(map.Grid.Owner));
                Assert.That(transform.ParentUid, Is.EqualTo(map.Grid.Owner));
                Assert.That(transform.Anchored, Is.True);
            }
            Assert.That(entities.GetComponent<ResearchClientComponent>(farm).Server, Is.Null);
            maps.InitializeMap(map.MapUid);
            Assert.That(entities.GetComponent<MetaDataComponent>(farm).EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));
            Assert.That(entities.GetComponent<ResearchClientComponent>(farm).Server, Is.EqualTo(fixedServer));
            Assert.That(entities.GetComponent<ResearchServerComponent>(fixedServer).Clients, Does.Contain(farm));
            Assert.That(entities.GetComponent<AnchorableComponent>(fixedServer).Flags & AnchorableFlags.Unanchorable, Is.EqualTo(AnchorableFlags.None));
            var ordinary = entities.SpawnEntity("ResearchAndDevelopmentServer", map.GridCoords.Offset(Vector2.UnitY));
            Assert.That(entities.GetComponent<AnchorableComponent>(ordinary).Flags & AnchorableFlags.Unanchorable, Is.EqualTo(AnchorableFlags.Unanchorable));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AiCoreOccupancyFollowsMindThroughHeldBrainEyeAndRemoteBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var core = entities.SpawnEntity("PlayerStationAiEmpty", map.GridCoords);
            var component = entities.GetComponent<StationAiCoreComponent>(core);
            var ai = entities.System<SharedStationAiSystem>();
            var minds = entities.System<MindSystem>();
            var user = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(Vector2.UnitX));
            var tool = entities.SpawnEntity("Wrench", map.GridCoords);
            Assert.That(entities.GetComponent<AnchorableComponent>(core).Flags & AnchorableFlags.Unanchorable, Is.EqualTo(AnchorableFlags.Unanchorable));
            void AssertBlocked(bool blocked)
            {
                var start = new UnanchorAttemptEvent(user, tool);
                entities.EventBus.RaiseLocalEvent(core, start);
                var completion = new UnanchorCompletionAttemptEvent(user, tool);
                entities.EventBus.RaiseLocalEvent(core, completion);
                Assert.That(start.Cancelled, Is.EqualTo(blocked));
                Assert.That(completion.Cancelled, Is.EqualTo(blocked));
                Assert.That(ai.HasAssignedMind((core, component)), Is.EqualTo(blocked));
            }
            AssertBlocked(false);
            var brain = entities.SpawnEntity("StationAiBrain", map.GridCoords);
            var containers = entities.System<SharedContainerSystem>();
            Assert.That(containers.Insert(brain, containers.GetContainer(core, StationAiCoreComponent.Container)), Is.True);
            AssertBlocked(false);
            // This mind has no live session; absence of Actor is not proof of vacancy.
            var mind = minds.CreateMind(null);
            minds.TransferTo(mind, brain);
            AssertBlocked(true);
            var body = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(Vector2.UnitY));
            minds.TransferTo(mind, body);
            entities.GetComponent<StationAiHeldComponent>(brain).CurrentConnectedEntity = body;
            var remote = entities.EnsureComponent<AiRemoteControllerComponent>(body);
            remote.AiHolder = brain;
            remote.LinkedMind = mind;
            AssertBlocked(true);
            remote.AiHolder = null;
            remote.LinkedMind = null;
            entities.GetComponent<StationAiHeldComponent>(brain).CurrentConnectedEntity = null;
            Assert.That(component.RemoteEntity, Is.Not.Null);
            minds.TransferTo(mind, component.RemoteEntity!.Value);
            AssertBlocked(true);
            minds.TransferTo(mind, null, createGhost: false);
            AssertBlocked(false);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AiJoiningDuringWrenchDelayPreventsUnanchoringAtCompletion(bool joinDuringDelay)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid core = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.SetTile(map.Grid, new Vector2i(1, 0), map.Tile.Tile);
            core = entities.SpawnEntity("PlayerStationAiEmpty", map.GridCoords);
            var user = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(Vector2.UnitX));
            var tool = entities.SpawnEntity("Wrench", map.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, tool), Is.True);
            var use = new InteractUsingEvent(user, tool, core, map.GridCoords);
            entities.EventBus.RaiseLocalEvent(core, use);
            Assert.That(use.Handled, Is.True);
            Assert.That(entities.GetComponent<DoAfterComponent>(user).DoAfters, Has.Count.EqualTo(1));
            if (joinDuringDelay)
            {
                var brain = entities.SpawnEntity("StationAiBrain", map.GridCoords);
                var containers = entities.System<SharedContainerSystem>();
                Assert.That(containers.Insert(brain, containers.GetContainer(core, StationAiCoreComponent.Container)), Is.True);
                var minds = entities.System<MindSystem>();
                minds.TransferTo(minds.CreateMind(null), brain);
            }
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 3);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(core).Anchored, Is.EqualTo(joinDuringDelay)));
        await pair.CleanReturnAsync();
    }
}
