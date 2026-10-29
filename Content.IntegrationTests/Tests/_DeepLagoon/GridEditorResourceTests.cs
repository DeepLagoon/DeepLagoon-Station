using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Server.Administration.Managers;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.GameObjects;
using ClientEditor = Content.Client._DeepLagoon.Mapping.GridEditorSystem;
using ServerEditor = Content.Server._DeepLagoon.Mapping.GridEditorSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GridEditorResourceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(5)]
    [TestCase(8)]
    public async Task LargeTabsStreamBoundedSnapshotsAndTrackSameTickTileChanges(int count)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        const int size = 96;
        var grids = new List<EntityUid>();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var editor = entities.System<ServerEditor>();
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var index = 0; index < count; index++)
            {
                var grid = maps.CreateGridEntity(mapId); grids.Add(grid.Owner);
                var tiles = new List<(Vector2i, Tile)>();
                for (var x = 0; x < size; x++) for (var y = 0; y < size; y++) tiles.Add((new Vector2i(x, y), tile));
                maps.SetTiles(grid, grid.Comp, tiles);
                editor.Open(player, grid);
                Assert.That(typeof(ServerEditor).GetMethod("NeedsSynchronization", Private)!.Invoke(editor, new object[] { grid.Owner }), Is.False);
            }
            // Two mutations in one simulation tick must invalidate a cached tile list.
            var first = grids[0];
            var component = entities.GetComponent<MapGridComponent>(first);
            maps.SetTile(first, component, new Vector2i(1, 1), Tile.Empty);
            Assert.That(typeof(ServerEditor).GetMethod("NeedsSynchronization", Private)!.Invoke(editor, new object[] { first }), Is.True);
            typeof(ServerEditor).GetMethod("SynchronizeGrid", Private)!.Invoke(editor, new object[] { first, null, null });
            maps.SetTile(first, component, new Vector2i(1, 1), tile);
            Assert.That(typeof(ServerEditor).GetMethod("NeedsSynchronization", Private)!.Invoke(editor, new object[] { first }), Is.True);
            typeof(ServerEditor).GetMethod("SynchronizeGrid", Private)!.Invoke(editor, new object[] { first, null, null });
        });
        await pair.RunTicksSync(50);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var tabs = (IDictionary) typeof(ClientEditor).GetField("_tabs", Private)!.GetValue(editor);
            Assert.That(tabs.Count, Is.EqualTo(count));
            var window = (Content.Client._DeepLagoon.Mapping.GridEditorWindow) typeof(ClientEditor).GetField("_window", Private)!.GetValue(editor);
            var web = window.Panel.Web;
            var snapshots = new Dictionary<string, int>();
            var completed = new HashSet<string>();
            var maximumChunk = 0;
            var initialQueue = (ICollection) typeof(GameWebView).GetField("_outgoing", Private)!.GetValue(web);
            Assert.That(initialQueue.Count, Is.LessThan(64), "No full map JSON should be queued before Ready");
            void Receive(string type, string payload)
            {
                if (type != "update") return;
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (!root.TryGetProperty("gridEditorSnapshot", out var snapshot)) return;
                var token = root.GetProperty("gridEditorToken").GetString();
                var phase = snapshot.GetProperty("phase").GetString();
                if (phase == "start") snapshots[token] = 0;
                if (phase == "chunk")
                {
                    var items = root.GetProperty("data").GetProperty("items").GetArrayLength();
                    maximumChunk = Math.Max(maximumChunk, items); snapshots[token] += items;
                }
                if (phase == "complete") completed.Add(token);
            }
            var sending = typeof(GameWebView).GetEvent("Sending", Private)!;
            sending.GetAddMethod(true)!.Invoke(web, new object[] { (Action<string, string>) Receive });
            typeof(GameWebView).GetField("_ready", Private)!.SetValue(web, true);
            ((Action) typeof(GameWebView).GetField("Ready", Private)!.GetValue(web))();
            for (var frame = 0; frame < count * 16; frame++) editor.FrameUpdate(1f / 60);
            sending.GetRemoveMethod(true)!.Invoke(web, new object[] { (Action<string, string>) Receive });
            Assert.That(completed.Count, Is.EqualTo(count));
            Assert.That(snapshots.Values, Is.All.EqualTo(size * size));
            Assert.That(maximumChunk, Is.LessThanOrEqualTo(1024));
            Assert.That(editor.State, Is.Not.Null);
        });
        await pair.CleanReturnAsync();
    }
}
