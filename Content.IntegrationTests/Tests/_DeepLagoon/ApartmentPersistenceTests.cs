using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Decals;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Spreader;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Chemistry.Components;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.GameTicking;
using Content.Shared.MagicMirror;
using Content.Shared.Paper;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Moq;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentPersistenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task ActivePuddleSurvivesUnloadAndSpreadsAfterRestore()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var puddles = entities.System<PuddleSystem>();
            var body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];

            var spreader = entities.GetComponent<SpreaderGridComponent>(home.Grid);
            spreader.UpdateSpacing = 2;
            var volume = FixedPoint2.New(100);
            Assert.That(puddles.TrySpillAt(new EntityCoordinates(home.Grid, 2.5f, 7.5f), new Solution("Wine", volume),
                out var puddle, sound: false), Is.True);
            Assert.That(entities.HasComponent<ActiveEdgeSpreaderComponent>(puddle), Is.True);
            Assert.That(spreader.SpreadQueues[new("Puddle")].Any(entry => entry.Owner == puddle), Is.True);

            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            Unload(apartments, home);
            Assert.That(entities.EntityExists(home.Grid), Is.False);
            Assert.That(entities.EntityExists(puddle), Is.False);
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(apartments)!;
            Assert.That(store.LoadSnapshot(home.Owner.UserId, home.Template.ID)!.Grid, Does.Not.Contain("spreadQueues:"));

            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            var restored = apartments.Instances[session.UserId];
            var restoredPuddle = entities.EntityQuery<PuddleComponent, TransformComponent>()
                .Single(entry => entry.Item2.GridUid == restored.Grid).Item2.Owner;
            Assert.That(puddles.CurrentVolume(restoredPuddle), Is.EqualTo(volume));
            Assert.That(entities.HasComponent<ActiveEdgeSpreaderComponent>(restoredPuddle), Is.True);
            var restoredSpreader = entities.GetComponent<SpreaderGridComponent>(restored.Grid);
            Assert.That(restoredSpreader.UpdateSpacing, Is.EqualTo(2f));
            Assert.That(restoredSpreader.SpreadQueues[new("Puddle")].Select(entry => entry.Owner), Is.EqualTo(new[] { restoredPuddle }));

            entities.System<SpreaderSystem>().Update(restoredSpreader.UpdateSpacing);
            Assert.That(puddles.CurrentVolume(restoredPuddle), Is.LessThan(volume));
            Assert.That(entities.EntityQuery<PuddleComponent, TransformComponent>()
                .Any(entry => entry.Item2.GridUid == restored.Grid && entry.Item2.Owner != restoredPuddle), Is.True);
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CheckpointsSkipFrozenApartmentsAndRestoreMirrorWithoutItsInteraction()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(apartments)!;
            var body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);

            var now = TimeSpan.FromMinutes(1);
            var timingField = typeof(ApartmentSystem).GetField("_timing", Private)!;
            var originalTiming = timingField.GetValue(apartments);
            var clock = new Mock<IGameTiming>();
            clock.SetupGet(timing => timing.RealTime).Returns(() => now);
            clock.SetupGet(timing => timing.TickPeriod).Returns(TimeSpan.FromSeconds(1d / 30));
            timingField.SetValue(apartments, clock.Object);
            try
            {
                Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
                var home = apartments.Instances[session.UserId];
                var mirror = FindMirror(home.Grid);
                mirror.DoAfter = new DoAfterId(body, 1);
                var paper = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, Vector2.One));
                entities.GetComponent<PaperComponent>(paper).Content = "Checkpoint contents";

                now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59);
                apartments.Update(0);
                AwaitMaintenance(apartments);
                Assert.That(store.LoadSnapshot(home.Owner.UserId, home.Template.ID), Is.Null);
                now += TimeSpan.FromSeconds(1);
                apartments.Update(0);
                AwaitMaintenance(apartments);
                var checkpoint = store.LoadSnapshot(home.Owner.UserId, home.Template.ID);
                Assert.That(checkpoint?.Grid, Does.Contain("Checkpoint contents").And.Contain("N14JunkMirror"));
                Assert.That(mirror.DoAfter, Is.Not.Null, "Saving must preserve the running interaction in memory.");

                entities.GetComponent<PaperComponent>(paper).Content = "Contents saved before unload";
                now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59);
                Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
                Assert.That(home.Frozen, Is.True);
                now += TimeSpan.FromSeconds(1);
                apartments.Update(0);
                AwaitMaintenance(apartments);
                Assert.That(store.LoadSnapshot(home.Owner.UserId, home.Template.ID)!.Grid, Is.EqualTo(checkpoint!.Grid));

                now += TimeSpan.FromSeconds(pair.Server.CfgMan.GetCVar(ApartmentCVars.EmptyTimeout));
                apartments.Update(0);
                AwaitMaintenance(apartments);
                Assert.That(entities.EntityExists(home.Grid), Is.False);
                Assert.That(store.LoadSnapshot(home.Owner.UserId, home.Template.ID)!.Grid, Does.Contain("Contents saved before unload"));
                Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
                var restored = apartments.Instances[session.UserId];
                Assert.That(FindMirror(restored.Grid).DoAfter, Is.Null);
                Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            }
            finally
            {
                timingField.SetValue(apartments, originalTiming);
                pair.Server.PlayerMan.SetAttachedEntity(session, null);
                entities.DeleteEntity(body);
            }

            MagicMirrorComponent FindMirror(EntityUid grid)
            {
                return entities.EntityQuery<MagicMirrorComponent, TransformComponent>()
                    .First(entry => entry.Item2.GridUid == grid && entities.GetComponent<MetaDataComponent>(entry.Item2.Owner).EntityPrototype?.ID == "N14JunkMirror").Item1;
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FailedUnloadRetainsGridAndWaitsBeforeRetrying()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var apartments = pair.Server.EntMan.System<ApartmentSystem>();
            var owner = pair.Server.PlayerMan.Sessions.Single().UserId;
            var home = apartments.PrepareElevatorApartment(owner);
            apartments.RefreshActivity(home);
            home.IdleSince = TimeSpan.FromMinutes(-10);

            var storeField = typeof(ApartmentSystem).GetField("_store", Private)!;
            var timingField = typeof(ApartmentSystem).GetField("_timing", Private)!;
            var originalStore = (IApartmentLayoutStore) storeField.GetValue(apartments)!;
            var originalTiming = timingField.GetValue(apartments);
            var now = TimeSpan.FromMinutes(10);
            var clock = new Mock<IGameTiming>();
            clock.SetupGet(timing => timing.RealTime).Returns(() => now);
            clock.SetupGet(timing => timing.TickPeriod).Returns(TimeSpan.FromSeconds(1d / 30));
            var store = new Mock<IApartmentLayoutStore>();
            var fail = true;
            store.Setup(s => s.SaveCapturedSnapshotAsync(owner.UserId, home.Template.ID,
                It.IsAny<ApartmentSnapshotCapture>(), It.IsAny<ApartmentSnapshot>(), Guid.Empty))
                .Returns(() => fail ? Task.FromException<ApartmentSnapshotWriteResult>(new InvalidOperationException("Simulated apartment write failure"))
                    : Task.FromResult(default(ApartmentSnapshotWriteResult)));

            var failureLevel = pair.ServerLogHandler.FailureLevel;
            try
            {
                pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;
                storeField.SetValue(apartments, store.Object);
                timingField.SetValue(apartments, clock.Object);
                apartments.Update(0);
                AwaitMaintenance(apartments);
                Assert.That(apartments.Instances[owner], Is.SameAs(home));
                Assert.That(home.RetrySaveAt, Is.EqualTo(now + TimeSpan.FromMinutes(1)));

                now += TimeSpan.FromSeconds(59);
                apartments.Update(0);
                AwaitMaintenance(apartments);
                Assert.That(store.Invocations, Has.Count.EqualTo(1));
                Assert.That(pair.Server.EntMan.EntityExists(home.Grid), Is.True);

                fail = false;
                now += TimeSpan.FromSeconds(1);
                apartments.Update(0);
                AwaitMaintenance(apartments);
                Assert.That(store.Invocations, Has.Count.EqualTo(2));
                Assert.That(apartments.Instances.ContainsKey(owner), Is.False);
                Assert.That(pair.Server.EntMan.EntityExists(home.Grid), Is.False);
            }
            finally
            {
                pair.ServerLogHandler.FailureLevel = failureLevel;
                storeField.SetValue(apartments, originalStore);
                timingField.SetValue(apartments, originalTiming);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EditorStoragePreservesOriginalContentsAcrossUnloadAndRoundEnd()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentBasic");
            pair.Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic").ExistingOnly = false;
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var containers = entities.System<SharedContainerSystem>();
            var body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];
            Assert.That(apartments.OpenEditor(session), Is.True);
            var mapped = home.MapFurniture.Values.First(item => item.Prototype == "PottedPlant2");
            var furniture = home.Furniture[mapped.Baseline.Id];
            var paper = entities.SpawnEntity("Paper", new EntityCoordinates(home.Grid, Vector2.One));
            entities.GetComponent<PaperComponent>(paper).Content = "Keep this inside stored furniture";
            Assert.That(containers.Insert(paper, containers.EnsureContainer<Container>(furniture, "keepsakes")), Is.True);
            var layout = (List<ApartmentPlacement>) typeof(ApartmentSystem).GetMethod("CurrentLayout", Private)!.Invoke(apartments, new object[] { home })!;
            var editors = (Dictionary<NetUserId, Guid>) typeof(ApartmentSystem).GetField("_editors", Private)!.GetValue(apartments)!;
            Assert.That(apartments.Apply(session, editors[session.UserId], home.Delta.Revision, layout.Where(item => item.Id != mapped.Baseline.Id).ToArray()), Is.Null);
            Assert.That(entities.GetComponent<ApartmentFurnitureIdentityComponent>(furniture).Stored, Is.True);
            Assert.That(containers.GetContainer(home.Grid, "apartment-furniture-storage").Contains(furniture), Is.True);
            Assert.That(containers.GetContainer(furniture, "keepsakes").Contains(paper), Is.True);
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            Unload(apartments, home);
            typeof(ApartmentSystem).GetMethod("Cleanup", Private)!.Invoke(apartments, null);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            home = apartments.Instances[session.UserId];
            var restored = home.Furniture[mapped.Baseline.Id];
            Assert.That(entities.GetComponent<ApartmentFurnitureIdentityComponent>(restored).Stored, Is.True);
            var restoredPaper = containers.GetContainer(restored, "keepsakes").ContainedEntities.Single();
            Assert.That(entities.GetComponent<PaperComponent>(restoredPaper).Content, Is.EqualTo("Keep this inside stored furniture"));
            Assert.That(apartments.OpenEditor(session), Is.True);
            var current = (List<ApartmentPlacement>) typeof(ApartmentSystem).GetMethod("CurrentLayout", Private)!.Invoke(apartments, new object[] { home })!;
            Assert.That(current.Any(item => item.Id == mapped.Baseline.Id), Is.False);
            typeof(ApartmentSystem).GetField("_lastApply", Private)!.GetValue(apartments)!.GetType().GetMethod("Clear")!.Invoke(typeof(ApartmentSystem).GetField("_lastApply", Private)!.GetValue(apartments), null);
            Assert.That(apartments.Apply(session, editors[session.UserId], home.Delta.Revision, current.Append(mapped.Baseline.Copy()).ToArray()), Is.Null);
            Assert.That(home.Furniture[mapped.Baseline.Id], Is.EqualTo(restored));
            Assert.That(entities.GetComponent<ApartmentFurnitureIdentityComponent>(restored).Stored, Is.False);
            Assert.That(containers.GetContainer(restored, "keepsakes").ContainedEntities.Single(), Is.EqualTo(restoredPaper));
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ContentsDecalsAndFurnitureStateSurviveUnloadAndRoundCleanup()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitPost(() =>
        {
            // Keep the legacy map geometry required by this furniture fixture.
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentBasic");
            pair.Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic").ExistingOnly = false;
        });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
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
        await pair.Server.WaitPost(() =>
        {
            // Keep the legacy map geometry required by this furniture fixture.
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentBasic");
            pair.Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic").ExistingOnly = false;
        });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
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

    [Test]
    public async Task ReentryReservesThePositionOfAnApartmentStillBeingDeleted()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var old = apartments.Instances[session.UserId];
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            old.IdleSince = TimeSpan.FromSeconds(-10000);
            typeof(ApartmentSystem).GetField("_nextUpdate", Private)!.SetValue(apartments, TimeSpan.Zero);
            apartments.Update(1);
            typeof(ApartmentSystem).GetMethod("AwaitApartmentOperations", Private)!.Invoke(apartments, null);
            var deletion = entities.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>();
            Assert.That(deletion.IsPending(old.Grid), Is.True);
            Assert.That(entities.EntityExists(old.Grid), Is.True);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            var fresh = apartments.Instances[session.UserId];
            Assert.That(fresh.Grid, Is.Not.EqualTo(old.Grid));
            var oldBounds = new Box2(old.Position, old.Position + new Vector2(old.Template.Width, old.Template.Height));
            var newBounds = new Box2(fresh.Position, fresh.Position + new Vector2(fresh.Template.Width, fresh.Template.Height));
            Assert.That(oldBounds.Intersects(newBounds), Is.False, "Pending map deletion must still reserve its space.");
            for (var i = 0; i < 1000 && deletion.PendingCount > 0; i++) deletion.Update(0);
            Assert.That(entities.EntityExists(old.Grid), Is.False);
            Assert.That(entities.EntityExists(fresh.Grid), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(fresh.Grid));
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
        AwaitMaintenance(apartments);
        var entities = Robust.Shared.IoC.IoCManager.Resolve<IEntityManager>();
        Assert.That(entities.EntityExists(home.Grid), Is.False);
    }
    private static void AwaitMaintenance(ApartmentSystem apartments)
    {
        var pending = (Task)typeof(ApartmentSystem).GetField("_maintenanceSave", Private)!.GetValue(apartments)!;
        Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Asynchronous.ITaskManager>().BlockWaitOnTask(pending);
        var entities = Robust.Shared.IoC.IoCManager.Resolve<IEntityManager>();
        var deletion = entities.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>();
        for (var i = 0; i < 1000 && deletion.PendingCount > 0; i++) deletion.Update(0);
        Assert.That(deletion.PendingCount, Is.Zero);
    }

}
