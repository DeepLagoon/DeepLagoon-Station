using System.Linq;
using Content.Server.Stack;
using Content.Server.Storage.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class QuickPickupTests
{
    [Test]
    public async Task LightReplacerPicksUpFiftyWorkingCopiesOnTheClickedTile()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var entities = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var maps = server.System<SharedMapSystem>();
            maps.SetTile(map.Grid, new Vector2i(0, 0), map.Tile.Tile);
            maps.SetTile(map.Grid, new Vector2i(1, 0), map.Tile.Tile);
            var origin = new EntityCoordinates(map.Grid, 0.5f, 0.5f);
            var pile = new EntityCoordinates(map.Grid, 0.95f, 0.5f);
            var user = entities.SpawnEntity("MobHumanDummy", origin);
            var tool = entities.SpawnEntity("LightReplacerEmpty", origin);
            var bulbs = Enumerable.Range(0, 50).Select(_ => entities.SpawnEntity("LightBulb", pile)).ToArray();
            var neighbor = entities.SpawnEntity("LightBulb", new EntityCoordinates(map.Grid, 1.05f, 0.5f));
            var tube = entities.SpawnEntity("LightTube", pile);
            var broken = entities.SpawnEntity("LightBulb", pile);
            entities.GetComponent<LightBulbComponent>(broken).State = LightBulbState.Broken;
            var backpack = entities.SpawnEntity("ClothingBackpack", pile);
            var stored = entities.SpawnEntity("LightBulb", pile);
            Assert.That(server.System<StorageSystem>().Insert(backpack, stored, out _, playSound: false), Is.True);

            var click = new AfterInteractEvent(user, tool, bulbs[0], pile, canReach: true);
            entities.EventBus.RaiseLocalEvent(tool, click);

            var containers = server.System<SharedContainerSystem>();
            Assert.That(click.Handled, Is.True);
            Assert.That(containers.GetContainer(tool, "light_replacer_storage").ContainedEntities, Is.EquivalentTo(bulbs));
            Assert.That(containers.IsEntityInContainer(neighbor), Is.False);
            Assert.That(containers.IsEntityInContainer(tube), Is.False);
            Assert.That(containers.IsEntityInContainer(broken), Is.False);
            Assert.That(containers.GetContainer(backpack, StorageComponent.ContainerId).Contains(stored), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase("RPED", 16)]
    [TestCase("BluespaceRPED", 64)]
    public async Task PartExchangerPicksUpMatchingPartsAndLeavesOverflow(string prototype, int capacity)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var entities = server.EntMan;

        await server.WaitAssertion(() =>
        {
            server.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(0, 0), map.Tile.Tile);
            var origin = new EntityCoordinates(map.Grid, 0.5f, 0.5f);
            var pile = new EntityCoordinates(map.Grid, 0.8f, 0.5f);
            var user = entities.SpawnEntity("MobHumanDummy", origin);
            var tool = entities.SpawnEntity(prototype, origin);
            var storage = server.System<StorageSystem>();
            for (var i = 0; i < capacity - 1; i++)
            {
                var filler = entities.SpawnEntity("CapacitorStockPart", origin);
                Assert.That(storage.Insert(tool, filler, out _, playSound: false, stackAutomatically: false), Is.True);
            }

            var stacks = server.System<StackSystem>();
            var extra = entities.SpawnEntity("MatterBinStockPart", pile);
            var overflow = stacks.Spawn(5, "MatterBin", pile);
            var clicked = stacks.Spawn(23, "MatterBin", pile);
            var different = entities.SpawnEntity("AdvancedMatterBinStockPart", pile);

            var click = new AfterInteractEvent(user, tool, clicked, pile, canReach: true);
            entities.EventBus.RaiseLocalEvent(tool, click);

            var containers = server.System<SharedContainerSystem>();
            var contents = containers.GetContainer(tool, StorageComponent.ContainerId);
            Assert.That(click.Handled, Is.True);
            Assert.That(contents.ContainedEntities, Has.Count.EqualTo(capacity));
            Assert.That(contents.Contains(clicked), Is.True);
            Assert.That(entities.GetComponent<StackComponent>(clicked).Count, Is.EqualTo(25));
            Assert.That(entities.GetComponent<StackComponent>(extra).Count, Is.Zero);
            Assert.That(entities.GetComponent<StackComponent>(overflow).Count, Is.EqualTo(4));
            Assert.That(containers.IsEntityInContainer(overflow), Is.False);
            Assert.That(containers.IsEntityInContainer(different), Is.False);
        });

        await pair.CleanReturnAsync();
    }
}
