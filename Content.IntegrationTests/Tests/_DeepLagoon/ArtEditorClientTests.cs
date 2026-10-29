using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.Art;
using Content.Client._DeepLagoon.WebUI;
using Content.Server._DeepLagoon.Art;
using Content.Server.Crayon;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Art;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Moq;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ArtEditorClientTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task ClientSaveSurvivesArchiveFailureCloseAndReopenThenRetriesArchive()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, NoLoadTestPrototypes = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var realDb = pair.Server.ResolveDependency<IServerDbManager>();
        var failedDb = new Mock<IServerDbManager>();
        failedDb.Setup(db => db.ArchiveArtworkAsync(It.IsAny<ArchivedArtwork>())).ThrowsAsync(new IOException("Archive unavailable"));
        var system = entities.System<ArtSystem>();
        var dbField = typeof(ArtSystem).GetField("_db", Private)!;
        var previousLevel = system.Log.Level;
        EntityUid user = default, canvas = default, tool = default;
        var expectedColor = "";
        var baked = ArtDocument.Bake(JsonSerializer.Serialize(new { version = 1, title = "Сохранено из интерфейса", layers = new[] {
            new { name = "Слой 1", visible = true, opacity = 1, png = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create(), mask = "" } } }));
        void OpenCanvas() => entities.EventBus.RaiseLocalEvent(canvas, new InteractUsingEvent(user, tool, canvas, map.GridCoords));
        await pair.Client.WaitPost(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 2);
            cfg.SetCVar(WebUiCVars.WindowStyles, "ArtStudio=2");
        });
        await pair.Server.WaitAssertion(() =>
        {
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            canvas = entities.SpawnEntity("DLArtCanvas", map.GridCoords);
            tool = entities.SpawnEntity("CrayonBlue", map.GridCoords);
            var toolColor = entities.GetComponent<CrayonComponent>(tool).Color;
            expectedColor = toolColor.ToHexNoAlpha();
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, tool, animate: false), Is.True);
            dbField.SetValue(system, failedDb.Object);
            system.Log.Level = LogLevel.Fatal;
            OpenCanvas();
        });
        var netCanvas = entities.GetNetEntity(canvas);
        ArtEditorBoundUserInterface bui = null;
        var action = typeof(ArtEditorBoundUserInterface).GetMethod("Action", Private)!;
        var stateField = typeof(ArtEditorBoundUserInterface).GetField("_state", Private)!;
        async Task GetEditor()
        {
            await pair.RunTicksSync(30);
            await pair.Client.WaitAssertion(() =>
            {
                var client = pair.Client.EntMan;
                Assert.That(client.System<Robust.Client.GameObjects.UserInterfaceSystem>().TryGetOpenUi(client.GetEntity(netCanvas), ArtUiKey.Key, out bui), Is.True);
                var state = (ArtEditorState) stateField.GetValue(bui)!;
                Assert.That(state.Color, Is.EqualTo(expectedColor));
                var window = (ArtEditorWindow) typeof(ArtEditorBoundUserInterface).GetField("_window", Private)!.GetValue(bui)!;
                Assert.That(window.Panel.UsingNative, Is.False, "Canvas ignores global and per-window classic preferences");
                window.Panel.SwitchToNative();
                Assert.That(window.Panel.UsingNative, Is.False, "Even browser failure must not select the native canvas editor");
            });
        }
        async Task SaveFromClient(string document = null)
        {
            document ??= baked.Json;
            var count = (document.Length + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars;
            for (var index = 0; index < count; index++)
            {
                var part = index;
                await pair.Client.WaitPost(() => action.Invoke(bui, new object[] { "save", new TguiData().Number("index", part).Number("count", count)
                    .String("text", document.Substring(part * ArtLimits.ChunkChars, Math.Min(ArtLimits.ChunkChars, document.Length - part * ArtLimits.ChunkChars))).ToString() }));
                await pair.RunTicksSync(2);
            }
        }
        async Task Saved(string expectedStatus)
        {
            var complete = false;
            for (var i = 0; i < 150 && !complete; i++)
            {
                await pair.RunTicksSync(5);
                await pair.Server.WaitPost(() => complete = entities.System<Robust.Server.GameObjects.UserInterfaceSystem>().TryGetUiState<ArtEditorState>(canvas, ArtUiKey.Key, out var state)
                    && state.Result > 0 && state.Status == expectedStatus);
                if (!complete) await Task.Delay(20);
            }
            Assert.That(complete, Is.True, expectedStatus);
        }
        try
        {
            await GetEditor();
            await SaveFromClient();
            await Saved("dl-art-db-failed");
            await pair.Client.WaitPost(() => action.Invoke(bui, new object[] { "close", "{}" }));
            await pair.RunTicksSync(30);
            await pair.Server.WaitAssertion(() =>
            {
                var data = entities.GetComponent<ArtCanvasComponent>(canvas);
                Assert.That(data.Artist, Is.Null);
                Assert.That(data.Document, Is.EqualTo(baked.Json), "Archive failure must not discard a validated canvas save");
                Assert.That(data.SavedPreview, Is.EqualTo(baked.Preview));
                dbField.SetValue(system, realDb);
                system.Log.Level = previousLevel;
                OpenCanvas();
            });
            await GetEditor();
            await pair.Client.WaitAssertion(() => Assert.That(((ArtEditorState) stateField.GetValue(bui)!).Document, Is.EqualTo(baked.Json)));
            await pair.RunTicksSync(180);
            await SaveFromClient();
            await Saved("dl-art-saved");
            var pool = await realDb.GetArtworkPoolAsync();
            Assert.That(pool, Has.Count.EqualTo(1));
            Assert.That((await realDb.GetArtworkAsync(pool.Single())).Document, Is.EqualTo(baked.Json));
            await pair.RunTicksSync(180);
            var empty = ArtDocument.Bake(ArtLimits.EmptyDocument);
            await SaveFromClient(empty.Json);
            await Saved("dl-art-canvas-saved");
            await pair.Client.WaitPost(() => action.Invoke(bui, new object[] { "close", "{}" }));
            await pair.RunTicksSync(30);
            await pair.Server.WaitAssertion(() =>
            {
                var data = entities.GetComponent<ArtCanvasComponent>(canvas);
                Assert.That(data.Document, Is.EqualTo(empty.Json), "Saving an erased canvas must not restore the old painting on close");
                Assert.That(data.RevisionHash, Is.Empty);
            });
            Assert.That(await realDb.GetArtworkPoolAsync(), Has.Count.EqualTo(1), "Empty canvases are saved locally without adding empty archive entries");
        }
        finally
        {
            await pair.Server.WaitPost(() => { dbField.SetValue(system, realDb); system.Log.Level = previousLevel; });
        }
        await pair.CleanReturnAsync();
    }
}
