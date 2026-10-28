using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.Administration.Managers;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Robust.Shared.Network;
using Editor = Content.Server._DeepLagoon.Mapping.GridEditorSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GridEditorAnchoringTests
{
    [Test]
    public async Task MovingAndCopyingWallSelectionsPreservesAnchoring()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var walls = new List<NetEntity>();
        Action prepare = null!;
        Action apply = null!;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId, runMapInit: true);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(grid, grid.Comp, Enumerable.Range(0, 8).SelectMany(x => Enumerable.Range(0, 3)
                .Select(y => (new Vector2i(x, y), floor))).ToList());
            var transforms = entities.System<SharedTransformSystem>();
            transforms.SetLocalPosition(grid, new Vector2(24, -12));
            transforms.SetLocalRotation(grid, Angle.FromDegrees(30));
            var observer = entities.SpawnEntity("MobObserver", new EntityCoordinates(grid, new Vector2(3.5f, 1.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(player, observer);
            prepare = () =>
            {
            var prototypes = new[] { "WallSolid", "WallShuttle", "WallShuttleDiagonal" };
            foreach (var (prototype, y) in prototypes.Select((prototype, y) => (prototype, y)))
            {
                var uid = entities.SpawnEntity(prototype, new EntityCoordinates(grid, new Vector2(.5f, y + .5f)));
                Assert.That(transforms.AnchorEntity(uid), Is.True);
                entities.Dirty(uid, entities.GetComponent<TransformComponent>(uid));
                walls.Add(entities.GetNetEntity(uid));
            }
            var editor = entities.System<Editor>();
            Assert.That(editor.Open(player, grid), Does.Contain("открыт"));
            var draft = ((IDictionary) typeof(Editor).GetField("_drafts", flags)!.GetValue(editor)!)[player]!;
            var token = (Guid) draft.GetType().GetField("Token")!.GetValue(draft)!;
            var items = (List<GridEditorItem>) draft.GetType().GetField("Items")!.GetValue(draft)!;
            var selection = items.Where(item => item.Kind == "entity").ToArray();
            Assert.That(selection.All(item => item.Anchored), Is.True);
            var patch = selection.Select(item => new GridEditorItem { Id = item.Id, Kind = item.Kind,
                Prototype = item.Prototype, X = item.X + 2, Y = item.Y, Rotation = 90, Anchored = item.Anchored }).ToList();
            patch.AddRange(selection.Select(item => new GridEditorItem { Id = "new_copy_" + item.Id, Kind = item.Kind,
                Prototype = item.Prototype, X = item.X + 4, Y = item.Y, Rotation = 180, Anchored = item.Anchored }));
            patch.AddRange(Enumerable.Range(0, 3).Select(y => new GridEditorItem { Id = $"t:0:{y}", Kind = "remove" }));
            apply = () =>
            {
            typeof(Editor).GetMethod("Apply", flags)!.Invoke(editor, new object[] {
                new GridEditorApplyEvent(token, patch, patch: true), new EntitySessionEventArgs(player) });
            var known = (Dictionary<string, EntityUid>) draft.GetType().GetField("KnownEntities")!.GetValue(draft)!;
            foreach (var item in selection) walls.Add(entities.GetNetEntity(known["new_copy_" + item.Id]));
            foreach (var wall in walls)
            {
                var uid = entities.GetEntity(wall);
                var transform = entities.GetComponent<TransformComponent>(uid);
                Assert.That(transform.Anchored, Is.True);
                Assert.That(maps.IsAnchored(grid, maps.TileIndicesFor(grid, transform.Coordinates), uid), Is.True);
            }
            };
            };
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(prepare);
        await pair.RunTicksSync(30);
        TestContext.Progress.WriteLine("Initial walls replicated");
        await pair.Client.WaitAssertion(() =>
        {
            foreach (var wall in walls)
                Assert.That(pair.Client.EntMan.GetComponent<TransformComponent>(pair.Client.EntMan.GetEntity(wall)).Anchored, Is.True);
        });
        await pair.Server.WaitAssertion(apply);
        TestContext.Progress.WriteLine("Wall selection applied");
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            foreach (var wall in walls)
            {
                var transform = entities.GetComponent<TransformComponent>(entities.GetEntity(wall));
                Assert.That(transform.Anchored, Is.True, wall.ToString());
                Assert.That(transform.ParentUid, Is.EqualTo(transform.GridUid));
                Assert.That(transform.LocalPosition.X, Is.EqualTo(walls.IndexOf(wall) < 3 ? 2.5f : 4.5f));
            }
        });
        await pair.Server.WaitAssertion(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ApplyingMovesAndNewAnchoredStructuresReplicatesValidGridParents(bool residentialMap)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default;
        NetEntity panel = default;
        Guid token = default;
        string panelId = "";
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId);
            Entity<MapGridComponent> mapGrid;
            if (residentialMap)
            {
                Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(mapId,
                    new ResPath("/Maps/_DeepLagoon/Apartments/residential_stop.yml"), out var loaded), Is.True);
                mapGrid = loaded!.Value;
            }
            else mapGrid = map.CreateGridEntity(mapId);
            grid = mapGrid.Owner;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 8; x++) for (var y = 0; y < 4; y++) tiles.Add((new(x, y), floor));
            map.SetTiles(grid, mapGrid.Comp, tiles);
            var body = entities.SpawnEntity("MobObserver", new EntityCoordinates(grid, new Vector2(.5f, 1.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(player, body);
            var uid = entities.SpawnEntity("DLApartmentElevatorPanel", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            panel = entities.GetNetEntity(uid);
            var editor = entities.System<Editor>();
            Assert.That(editor.Open(player, grid), Does.Contain("открыт"));
            var drafts = (IDictionary)typeof(Editor).GetField("_drafts", flags)!.GetValue(editor)!;
            var draft = drafts[player]!;
            token = (Guid)draft.GetType().GetField("Token")!.GetValue(draft)!;
            panelId = ((List<GridEditorItem>)draft.GetType().GetField("Items")!.GetValue(draft)!).Single(i => i.Id == "e:" + panel).Id;
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<Content.Client._DeepLagoon.Mapping.GridEditorSystem>();
            var catalog = (IEnumerable)editor.GetType().GetField("_catalog", flags)!.GetValue(editor)!;
            var entry = catalog.Cast<object>().Single(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)! == "DLApartmentElevatorPanel");
            Assert.That(entry.GetType().GetProperty("Anchored")!.GetValue(entry), Is.True);
        });
        for (var step = 1; step <= 4; step++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var player = pair.Server.PlayerMan.Sessions.Single();
                var items = new List<GridEditorItem> {
                    new() { Id = panelId, Kind = "entity", Prototype = "DLApartmentElevatorPanel", X = step + .5f, Y = .5f, Rotation = step * 90, Anchored = step != 2 },
                    new() { Id = "new_door_" + step, Kind = "entity", Prototype = "Airlock", X = step + .5f, Y = 2.5f, Anchored = true }
                };
                typeof(Editor).GetMethod("Apply", flags)!.Invoke(entities.System<Editor>(), new object[] {
                    new GridEditorApplyEvent(token, items, patch: true), new EntitySessionEventArgs(player) });
            });
            await pair.RunTicksSync(30);
            await pair.Client.WaitAssertion(() =>
            {
                var entities = pair.Client.EntMan;
                var uid = entities.GetEntity(panel);
                var transform = entities.GetComponent<TransformComponent>(uid);
                Assert.That(transform.Anchored, Is.EqualTo(step != 2));
                Assert.That(transform.ParentUid, Is.EqualTo(transform.GridUid));
                Assert.That(entities.HasComponent<MapGridComponent>(transform.ParentUid), Is.True);
                Assert.That(transform.LocalPosition, Is.EqualTo(new Vector2(step + .5f, .5f)));
            });
        }
        await pair.Server.WaitAssertion(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }
}
