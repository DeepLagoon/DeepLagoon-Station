using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.Art;
using Content.Client._DeepLagoon.WebUI;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Art;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Shared.GameObjects;
using ClientUi = Robust.Client.GameObjects.UserInterfaceSystem;
using ServerUi = Robust.Server.GameObjects.UserInterfaceSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ArtDraftRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task ClientDraftSurvivesLostToolAndForcedCloseWithoutArchivingAndDiscardRejectsReplay()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, NoLoadTestPrototypes = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        EntityUid user = default, canvas = default, brush = default;
        ArtEditorBoundUserInterface editor = null;
        var stateField = typeof(ArtEditorBoundUserInterface).GetField("_state", Private)!;
        var action = typeof(ArtEditorBoundUserInterface).GetMethod("Action", Private)!;
        var writer = Guid.NewGuid().ToString("N");
        var epoch = 0; var version = 0;
        var draft = Content.Server._DeepLagoon.Art.ArtDocument.Bake(JsonSerializer.Serialize(new { version = 1, title = "Черновик", layers = new[] {
            new { name = "Чернила", visible = true, opacity = 1d, png = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create(), mask = "" },
            new { name = "Маска", visible = true, opacity = .5, png = "", mask = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create() } } }));
        var empty = Content.Server._DeepLagoon.Art.ArtDocument.Bake(ArtLimits.EmptyDocument);
        ArtCanvasComponent Canvas() => entities.GetComponent<ArtCanvasComponent>(canvas);
        ArtEditorState State()
        {
            Assert.That(entities.System<ServerUi>().TryGetUiState<ArtEditorState>(canvas, ArtUiKey.Key, out var state), Is.True);
            return state!;
        }
        void Open() => entities.EventBus.RaiseLocalEvent(canvas, new InteractUsingEvent(user, brush, canvas, map.GridCoords));
        await pair.Server.WaitAssertion(() =>
        {
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            canvas = entities.SpawnEntity("DLArtCanvas", map.GridCoords);
            brush = entities.SpawnEntity("DLArtBrush", map.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, brush, animate: false), Is.True);
            Open(); epoch = Canvas().DraftEpoch; version = Canvas().DraftVersion;
        });
        var netCanvas = entities.GetNetEntity(canvas);
        async Task GetEditor()
        {
            await pair.RunTicksSync(30);
            await pair.Client.WaitAssertion(() => Assert.That(pair.Client.EntMan.System<ClientUi>()
                .TryGetOpenUi(pair.Client.EntMan.GetEntity(netCanvas), ArtUiKey.Key, out editor), Is.True));
        }
        async Task Send(string kind, string document, int sequence = 1, int? sentEpoch = null)
        {
            var count = (document.Length + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars;
            for (var i = 0; i < count; i++)
            {
                var index = i;
                await pair.Client.WaitPost(() => action.Invoke(editor, new object[] { kind, new TguiData().Number("index", index).Number("count", count)
                    .String("writer", writer).Number("sequence", sequence).Number("epoch", sentEpoch ?? epoch).Number("version", version)
                    .String("text", document.Substring(index * ArtLimits.ChunkChars, Math.Min(ArtLimits.ChunkChars, document.Length - index * ArtLimits.ChunkChars))).ToString() }));
                await pair.RunTicksSync(2);
            }
        }
        async Task Until(Func<bool> predicate)
        {
            var complete = false;
            for (var i = 0; i < 150 && !complete; i++)
            {
                await pair.RunTicksSync(5); await pair.Server.WaitPost(() => complete = predicate());
                if (!complete) await Task.Delay(10);
            }
            Assert.That(complete, Is.True, "Expected art state was not reached");
        }
        await GetEditor();
        string recoveryKey = null;
        await pair.Client.WaitAssertion(() => recoveryKey = ((ArtEditorState)stateField.GetValue(editor)!).RecoveryKey);
        await Send("draft", draft.Json);
        await Until(() => Canvas().DraftSequence == 1);
        await pair.RunTicksSync(90);
        await Send("draft", empty.Json, 1);
        await pair.RunTicksSync(90);
        // Begin a newer multipart snapshot, then lose the tool before its last part arrives.
        await pair.Client.WaitPost(() => action.Invoke(editor, new object[] { "draft", new TguiData().Number("index", 0).Number("count", 2)
            .String("writer", writer).Number("sequence", 2).Number("epoch", epoch).Number("version", version)
            .String("text", draft.Json[..Math.Min(draft.Json.Length, ArtLimits.ChunkChars)]).ToString() }));
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Canvas().DraftDocument, Is.EqualTo(draft.Json), "Duplicate and incomplete snapshots must not replace the accepted draft");
            Assert.That(Canvas().Document, Is.EqualTo(ArtLimits.EmptyDocument));
            Assert.That(entities.GetComponent<ArtDisplayComponent>(canvas).Preview, Is.EqualTo(draft.Preview));
            Assert.That(entities.System<SharedHandsSystem>().TryDrop(user, brush), Is.True);
        });
        await Until(() => Canvas().Artist == null);
        Assert.That(await db.GetArtworkPoolAsync(), Is.Empty, "Autosave must not publish the draft");
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Canvas().DraftDocument, Is.EqualTo(draft.Json));
            Assert.That(entities.GetComponent<ArtDisplayComponent>(canvas).Preview, Is.EqualTo(draft.Preview), "Releasing the editor keeps the draft preview");
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, brush, animate: false), Is.True);
            Open();
            Assert.That(State().HasDraft, Is.True);
            Assert.That(State().Document, Is.EqualTo(draft.Json));
            Assert.That(State().SavedDocument, Is.EqualTo(ArtLimits.EmptyDocument));
            Assert.That(State().RecoveryKey, Is.EqualTo(recoveryKey));
        });
        await GetEditor();
        await pair.Client.WaitPost(() => action.Invoke(editor, new object[] { "discard", "{}" }));
        await Until(() => Canvas().DraftDocument.Length == 0 && Canvas().DraftEpoch != epoch);
        await Send("draft", draft.Json, 99, epoch);
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Canvas().DraftDocument, Is.Empty, "A delayed pre-discard draft cannot resurrect erased work");
            epoch = Canvas().DraftEpoch; version = Canvas().DraftVersion;
        });
        await Send("draft", draft.Json, 100);
        await Until(() => Canvas().DraftSequence == 100);
        await Send("save", draft.Json);
        await Until(() => State().Result > 0);
        Assert.That(await db.GetArtworkPoolAsync(), Has.Count.EqualTo(1));
        await pair.RunTicksSync(180);
        await pair.Server.WaitPost(() => { epoch = Canvas().DraftEpoch; version = Canvas().DraftVersion; });
        await Send("draft", empty.Json, 101);
        await Until(() => Canvas().DraftSequence == 101);
        await pair.Server.WaitPost(() => entities.System<ServerUi>().CloseUis(canvas));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Canvas().DraftDocument, Is.EqualTo(empty.Json), "A fully erased draft is still a recoverable draft");
            Assert.That(Canvas().Document, Is.EqualTo(draft.Json));
            Open();
            Assert.That(State().Document, Is.EqualTo(empty.Json));
            Assert.That(State().SavedDocument, Is.EqualTo(draft.Json));
        });
        await GetEditor();
        await pair.Client.WaitPost(() => action.Invoke(editor, new object[] { "discard", "{}" }));
        await Until(() => Canvas().DraftDocument.Length == 0);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Canvas().Document, Is.EqualTo(draft.Json));
            Assert.That(entities.GetComponent<ArtDisplayComponent>(canvas).Preview, Is.EqualTo(draft.Preview));
            entities.System<ServerUi>().CloseUis(canvas);
        });
        Assert.That(await db.GetArtworkPoolAsync(), Has.Count.EqualTo(1));
        await pair.CleanReturnAsync();
    }
}
