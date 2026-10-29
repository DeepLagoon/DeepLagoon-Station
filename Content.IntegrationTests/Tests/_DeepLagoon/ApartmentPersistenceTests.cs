using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Decals;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.GameTicking;
using Content.Shared.Paper;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentPersistenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task ContentsDecalsAndFurnitureStateSurviveUnloadAndRoundCleanup()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];
            var containers = entities.System<SharedContainerSystem>();
            var transform = entities.System<SharedTransformSystem>();
            var note = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, new Vector2(2.25f, 5.75f)));
            entities.GetComponent<PaperComponent>(note).Content = "A note left on the floor";
            transform.SetLocalRotation(note, Angle.FromDegrees(37));
            var bag = entities.SpawnEntity("ClothingBackpack", new EntityCoordinates(home.Grid, new Vector2(2.5f, 6.5f)));
            var inner = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, Vector2.One));
            entities.GetComponent<PaperComponent>(inner).Content = "Inside the backpack";
            Assert.That(containers.Insert(inner, containers.GetContainer(bag, StorageComponent.ContainerId)), Is.True);
            var mapped = home.MapFurniture.Values.First(item => item.Prototype == "PottedPlant2");
            var furniture = home.Furniture[mapped.Baseline.Id];
            var stash = containers.EnsureContainer<Container>(furniture, "keepsakes");
            var keepsake = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, Vector2.One));
            entities.GetComponent<PaperComponent>(keepsake).Content = "Inside template furniture";
            Assert.That(containers.Insert(keepsake, stash), Is.True);
            var decals = entities.System<DecalSystem>();
            var floorPaint = new Vector2(2, 5);
            var wallPaint = new Vector2(2, 6);
            var maps = entities.System<SharedMapSystem>();
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(home.Grid, entities.GetComponent<MapGridComponent>(home.Grid), new Vector2i(2, 6), tile);
            entities.SpawnEntity("WallSolid", new EntityCoordinates(home.Grid, wallPaint + new Vector2(.5f)));
            Assert.That(decals.TryAddDecal("heart", new EntityCoordinates(home.Grid, floorPaint), out _, Color.Red, cleanable: true), Is.True);
            Assert.That(decals.TryAddDecal("heart", new EntityCoordinates(home.Grid, wallPaint), out _, Color.Blue, Angle.FromDegrees(45), 4, true), Is.True);
            Assert.That(apartments.SaveApartment(home), Is.True);
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(apartments)!;
            Assert.That(store.LoadSnapshot(session.UserId.UserId, home.Template.ID)!.Grid, Does.Not.Contain("proto: MobHuman"));
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            Unload(apartments, home);
            Assert.That(apartments.Instances.ContainsKey(session.UserId), Is.False);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            AssertRestored();
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            typeof(ApartmentSystem).GetMethod("Cleanup", Private)!.Invoke(apartments, null);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            AssertRestored();
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);

            void AssertRestored()
            {
                var restored = apartments.Instances[session.UserId];
                var papers = entities.EntityQuery<PaperComponent, TransformComponent>().Where(pair => pair.Item2.GridUid == restored.Grid).ToArray();
                Assert.That(papers.Select(pair => pair.Item1.Content), Is.EquivalentTo(new[] { "A note left on the floor", "Inside the backpack", "Inside template furniture" }));
                var loose = papers.Single(pair => pair.Item1.Content == "A note left on the floor").Item2;
                Assert.That(loose.LocalPosition, Is.EqualTo(new Vector2(2.25f, 5.75f)));
                Assert.That(loose.LocalRotation.Degrees, Is.EqualTo(37).Within(.001));
                Assert.That(papers.Single(pair => pair.Item1.Content == "Inside the backpack").Item2.ParentUid, Is.Not.EqualTo(restored.Grid));
                var restoredFurniture = restored.Furniture[mapped.Baseline.Id];
                Assert.That(containers.GetContainer(restoredFurniture, "keepsakes").ContainedEntities.Count, Is.EqualTo(1));
                Assert.That(decals.GetDecalsInRange(restored.Grid, floorPaint + new Vector2(.5f), .1f).Single().Decal.Color, Is.EqualTo(Color.Red));
                var wall = decals.GetDecalsInRange(restored.Grid, wallPaint + new Vector2(.5f), .1f).Single().Decal;
                Assert.That(wall.Color, Is.EqualTo(Color.Blue));
                Assert.That(wall.Angle.Degrees, Is.EqualTo(45).Within(.001));
                Assert.That(wall.ZIndex, Is.EqualTo(4));
                Assert.That(wall.Cleanable, Is.True);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TrashCanBeRecoveredUntilRoundEndIncludingWhenApartmentIsUnloaded(bool unload)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];
            var containers = entities.System<SharedContainerSystem>();
            var bin = entities.SpawnEntity("DLApartmentTrashBin", new EntityCoordinates(home.Grid, new Vector2(2.5f, 5.5f)));
            var bag = entities.SpawnEntity("ClothingBackpack", new EntityCoordinates(home.Grid, Vector2.One));
            var nested = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, Vector2.One));
            entities.GetComponent<PaperComponent>(nested).Content = "Discard with bag";
            Assert.That(containers.Insert(nested, containers.GetContainer(bag, StorageComponent.ContainerId)), Is.True);
            Assert.That(containers.Insert(bag, containers.GetContainer(bin, StorageComponent.ContainerId)), Is.True);
            for (var i = 0; i < 2; i++)
            {
                var trash = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, Vector2.One));
                entities.GetComponent<PaperComponent>(trash).Content = $"Discard loose paper {i}";
                Assert.That(containers.Insert(trash, containers.GetContainer(bin, StorageComponent.ContainerId)), Is.True);
            }
            var recovered = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, Vector2.One));
            entities.GetComponent<PaperComponent>(recovered).Content = "Recovered before round end";
            var binContainer = containers.GetContainer(bin, StorageComponent.ContainerId);
            Assert.That(containers.Insert(recovered, binContainer), Is.True);
            Assert.That(containers.Remove(recovered, binContainer), Is.True);
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            Unload(apartments, home);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            home = apartments.Instances[session.UserId];
            bin = entities.EntityQuery<ApartmentTrashComponent, TransformComponent>().Single(pair => pair.Item2.GridUid == home.Grid).Item2.Owner;
            Assert.That(containers.GetContainer(bin, StorageComponent.ContainerId).Count, Is.EqualTo(3), "Unloading must not empty the bin during the same round.");
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            if (unload) Unload(apartments, home);
            entities.EventBus.RaiseEvent(EventSource.Local, new RoundEndedEvent(0, TimeSpan.Zero));
            typeof(ApartmentSystem).GetMethod("Cleanup", Private)!.Invoke(apartments, null);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            home = apartments.Instances[session.UserId];
            bin = entities.EntityQuery<ApartmentTrashComponent, TransformComponent>().Single(pair => pair.Item2.GridUid == home.Grid).Item2.Owner;
            Assert.That(containers.GetContainer(bin, StorageComponent.ContainerId).Count, Is.Zero);
            Assert.That(entities.GetComponent<StorageComponent>(bin).StoredItems, Is.Empty);
            Assert.That(entities.EntityQuery<PaperComponent, TransformComponent>().Where(pair => pair.Item2.GridUid == home.Grid).Select(pair => pair.Item1.Content),
                Is.EquivalentTo(new[] { "Recovered before round end" }));
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    private static void Unload(ApartmentSystem apartments, ApartmentSystem.Instance home)
    {
        home.IdleSince = TimeSpan.FromSeconds(-10000);
        typeof(ApartmentSystem).GetField("_nextUpdate", Private)!.SetValue(apartments, TimeSpan.Zero);
        apartments.Update(1);
    }
}
