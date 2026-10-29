using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Content.Client._DeepLagoon.Mapping;
using Content.Client._DeepLagoon.WebUI;
using Content.Server.Administration.Managers;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using ClientEditor = Content.Client._DeepLagoon.Mapping.GridEditorSystem;
using ServerEditor = Content.Server._DeepLagoon.Mapping.GridEditorSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GridEditorRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task DraftsSurviveDocumentRestartIndependentlyAndAppliedEditsNeverReturn()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var index = 0; index < 2; index++)
            {
                var grid = maps.CreateGridEntity(mapId);
                maps.SetTile(grid, grid.Comp, Vector2i.Zero, tile);
                entities.System<ServerEditor>().Open(player, grid);
            }
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var tabs = (IDictionary) typeof(ClientEditor).GetField("_tabs", Private)!.GetValue(editor)!;
            var tokens = tabs.Keys.Cast<Guid>().ToArray();
            Assert.That(tokens, Has.Length.EqualTo(2));
            var tabAction = typeof(ClientEditor).GetMethod("TabAction", Private)!;
            void Action(Guid token, string action, TguiData payload) => tabAction.Invoke(editor, new object[] { action, payload.String("tabToken", token.ToString()).ToString() });
            void Preview(Guid token, string id, string item, bool complete = true, int generation = 0)
            {
                Action(token, "previewStart", new TguiData().String("id", id).Number("count", 1).Number("chunks", 1).Number("generation", generation)
                    .Object("expansion", new TguiData().Number("left", 2).Number("right", 0).Number("top", 0).Number("bottom", 0)));
                if (complete) Action(token, "previewChunk", new TguiData().String("id", id).Number("index", 0)
                    .Array("items", new[] { new TguiData().String("id", item).String("kind", "entity").String("prototype", "ChairWood")
                        .Number("x", .5f).Number("y", .5f).String("entityData", string.Concat(Enumerable.Repeat("имя 🐟", 9000))) }));
            }
            Preview(tokens[0], "saved-a", "new_a");
            Preview(tokens[1], "saved-b", "new_b");
            Preview(tokens[0], "unfinished", "must-not-replace", false);
            var window = (GridEditorWindow) typeof(ClientEditor).GetField("_window", Private)!.GetValue(editor)!;
            var web = window.Panel.Web;
            var recovery = new Dictionary<string, StringBuilder>();
            var complete = new HashSet<string>();
            var versions = new Dictionary<string, int>();
            void Receive(string type, string payload)
            {
                if (type != "update") return;
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (!root.TryGetProperty("gridEditorSnapshot", out var snapshot)) return;
                var token = root.GetProperty("gridEditorToken").GetString()!;
                var phase = snapshot.GetProperty("phase").GetString();
                if (phase == "start")
                {
                    recovery[token] = new StringBuilder();
                    versions[token] = root.GetProperty("data").GetProperty("snapshotPatchVersion").GetInt32();
                }
                if (phase == "draft")
                {
                    var text = root.GetProperty("data").GetProperty("text").GetString()!;
                    Assert.That(text.Length, Is.LessThanOrEqualTo(32768));
                    Assert.That(char.IsHighSurrogate(text[^1]), Is.False, "Chunking must preserve Unicode scalar boundaries");
                    recovery[token].Append(text);
                }
                if (phase == "complete") complete.Add(token);
            }
            var sending = typeof(GameWebView).GetEvent("Sending", Private)!;
            sending.GetAddMethod(true)!.Invoke(web, new object[] { (Action<string, string>) Receive });
            typeof(GameWebView).GetField("_ready", Private)!.SetValue(web, true);
            void Restart()
            {
                recovery.Clear(); complete.Clear();
                ((Action) typeof(GameWebView).GetField("Ready", Private)!.GetValue(web)!)();
                for (var frame = 0; frame < 32; frame++) editor.FrameUpdate(1f / 60);
                Assert.That(complete.Count, Is.EqualTo(2));
            }
            Restart();
            foreach (var (token, expected) in new[] { (tokens[0], "new_a"), (tokens[1], "new_b") })
            {
                using var json = JsonDocument.Parse(recovery[token.ToString()].ToString());
                Assert.That(json.RootElement.GetProperty("edits")[0].GetProperty("id").GetString(), Is.EqualTo(expected));
                Assert.That(json.RootElement.GetProperty("edits")[0].GetProperty("entityData").GetString(),
                    Is.EqualTo(string.Concat(Enumerable.Repeat("имя 🐟", 9000))));
                Assert.That(json.RootElement.GetProperty("expansion").GetProperty("left").GetInt32(), Is.EqualTo(2));
            }
            Action(tokens[0], "activateTab", new TguiData());
            var firstTab = tabs[tokens[0]]!;
            var saved = (Dictionary<string, GridEditorItem>) firstTab.GetType().GetField("RecoveryEdits")!.GetValue(firstTab)!;
            var failed = saved.Values.Single();
            var patch = new GridEditorPatchEvent(tokens[0], new(), new(), 0, 0, 1, 1, 0, 0, 1, 1,
                applied: true, previewGeneration: 1, failedItems: new() { failed });
            typeof(ClientEditor).GetMethod("Patch", Private)!.Invoke(editor, new object[] { patch });
            Restart();
            using (var json = JsonDocument.Parse(recovery[tokens[0].ToString()].ToString()))
            {
                Assert.That(json.RootElement.GetProperty("edits")[0].GetProperty("id").GetString(), Is.EqualTo("new_a"), "An unsuccessful placement remains recoverable");
                Assert.That(json.RootElement.GetProperty("expansion").GetProperty("left").GetInt32(), Is.Zero);
            }
            Action(tokens[0], "activateTab", new TguiData());
            patch = new GridEditorPatchEvent(tokens[0], new(), new(), 0, 0, 1, 1, 0, 0, 1, 1, applied: true, previewGeneration: 2);
            typeof(ClientEditor).GetMethod("Patch", Private)!.Invoke(editor, new object[] { patch });
            Preview(tokens[0], "late-old-generation", "new_a", generation: 0);
            Restart();
            using (var json = JsonDocument.Parse(recovery[tokens[0].ToString()].ToString()))
            {
                Assert.That(json.RootElement.GetProperty("edits").GetArrayLength(), Is.Zero);
                Assert.That(json.RootElement.GetProperty("expansion").GetProperty("left").GetInt32(), Is.Zero);
                Assert.That(versions[tokens[0].ToString()], Is.EqualTo(2));
            }
            using (var json = JsonDocument.Parse(recovery[tokens[1].ToString()].ToString()))
                Assert.That(json.RootElement.GetProperty("edits")[0].GetProperty("id").GetString(), Is.EqualTo("new_b"));
            sending.GetRemoveMethod(true)!.Invoke(web, new object[] { (Action<string, string>) Receive });
        });
        await pair.CleanReturnAsync();
    }
}
