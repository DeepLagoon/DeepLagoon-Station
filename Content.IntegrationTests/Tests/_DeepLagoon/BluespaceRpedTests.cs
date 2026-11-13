using System.Linq;
using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.Stack;
using Content.Server.Storage.EntitySystems;
using Content.Shared.Exchanger;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class BluespaceRpedTests
{
    [Test]
    public async Task RemoteExchangeRequiresSightAndRangeAndPreservesTheRemainder()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var entities = server.EntMan;
        EntityCoordinates At(float x) => new(map.Grid, x + 0.5f, 0.5f);
        EntityUid user = default, tool = default, target = default, parts = default, wall = default;

        bool Exchange()
        {
            var interaction = new AfterInteractEvent(user, tool, target, At(15), canReach: false);
            entities.EventBus.RaiseLocalEvent(tool, interaction);
            return interaction.Handled;
        }

        await server.WaitAssertion(() =>
        {
            var maps = server.System<SharedMapSystem>();
            var transform = server.System<SharedTransformSystem>();
            var containers = server.System<SharedContainerSystem>();
            for (var x = 0; x <= 15; x++)
                maps.SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);

            user = entities.SpawnEntity("MobHumanDummy", At(0));
            tool = entities.SpawnEntity("BluespaceRPED", At(0));
            target = entities.SpawnEntity("MachineFrame", At(15));
            var frame = entities.GetComponent<MachineFrameComponent>(target);
            var board = entities.SpawnEntity("AutolatheMachineCircuitboard", At(15));
            Assert.That(containers.Insert(board, frame.BoardContainer), Is.True);
            server.System<MachineFrameSystem>().RegenerateProgress(frame);
            parts = server.System<StackSystem>().Spawn(5, "AdvancedMatterBin", At(0));
            Assert.That(server.System<StorageSystem>().Insert(tool, parts, out _, playSound: false), Is.True);

            Assert.That(Exchange(), Is.False);
            transform.SetCoordinates(user, At(9));
            wall = entities.SpawnEntity("WallSolid", At(12));
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(Exchange(), Is.False);
            Assert.That(entities.GetComponent<MachineFrameComponent>(target).Progress["MatterBin"], Is.Zero);
            Assert.That(entities.GetComponent<StackComponent>(parts).Count, Is.EqualTo(5));
            entities.DeleteEntity(wall);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(Exchange(), Is.True);
            var frame = entities.GetComponent<MachineFrameComponent>(target);
            var storage = entities.GetComponent<StorageComponent>(tool);
            var installed = frame.PartContainer.ContainedEntities.Single();
            Assert.That(entities.GetComponent<StackComponent>(installed).Count, Is.EqualTo(3));
            Assert.That(frame.Progress["MatterBin"], Is.EqualTo(3));
            Assert.That(storage.Container.Contains(parts), Is.True);
            Assert.That(entities.GetComponent<StackComponent>(parts).Count, Is.EqualTo(2));
            Assert.That(Exchange(), Is.False);
            Assert.That(frame.PartContainer.ContainedEntities.Single(), Is.EqualTo(installed));
            Assert.That(entities.EntityQuery<ExchangeTrailComponent>().Count(trail => trail.Target == target), Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }
}
