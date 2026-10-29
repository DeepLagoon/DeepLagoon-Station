using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Content.Server._DeepLagoon.Art;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Art;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Microsoft.Extensions.Primitives;
using Moq;
using Robust.Server.ServerStatus;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using UserInterfaceSystem = Robust.Server.GameObjects.UserInterfaceSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ArtTabletTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task BrowserTicketSaveRevisionAndRevocationUseTheActualHttpHandler()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, NoLoadTestPrototypes = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var system = entities.System<ArtSystem>();
        var ui = entities.System<UserInterfaceSystem>();
        EntityUid user = default, canvas = default, brush = default;
        ArtEditorState State()
        {
            Assert.That(ui.TryGetUiState<ArtEditorState>(canvas, ArtUiKey.Key, out var state), Is.True);
            return state!;
        }
        var document = JsonSerializer.Serialize(new { version = 1, title = "Планшет", layers = new[] {
            new { name = "Слой 1", visible = true, opacity = 1, png = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create(), mask = "" } } });
        void Message(string action, string text = "")
        {
            var revision = State().Revision;
            var count = Math.Max(1, (text.Length + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars);
            for (var index = 0; index < count; index++)
                entities.EventBus.RaiseLocalEvent(canvas, new ArtEditorMessage(action,
                    text.Substring(index * ArtLimits.ChunkChars, Math.Min(ArtLimits.ChunkChars, text.Length - index * ArtLimits.ChunkChars)),
                    revision, index, count) { Actor = user, UiKey = ArtUiKey.Key });
        }
        await pair.Server.WaitPost(() =>
        {
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(ArtCVars.TabletUrl, "http://localhost/art-tablet/");
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            canvas = entities.SpawnEntity("DLArtCanvas", map.GridCoords);
            brush = entities.SpawnEntity("DLArtBrush", map.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, brush, animate: false), Is.True);
            entities.EventBus.RaiseLocalEvent(canvas, new InteractUsingEvent(user, brush, canvas, map.GridCoords));
            Message("tablet", document);
        });
        for (var i = 0; i < 150; i++)
        {
            var active = false;
            await pair.RunTicksSync(5);
            await pair.Server.WaitPost(() => active = State().TabletActive);
            if (active) break;
            await Task.Delay(10);
        }
        string Ticket()
        {
            var leases = (IDictionary)typeof(ArtSystem).GetField("_tabletLeases", Private)!.GetValue(system)!;
            return new Uri((string)leases.Values.Cast<object>().Single().GetType().GetField("Url")!
                .GetValue(leases.Values.Cast<object>().Single())!).Fragment[1..];
        }
        var ticket = "";
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(State().TabletAvailable && State().TabletActive, Is.True);
            ticket = Ticket();
            Assert.That(ticket, Has.Length.EqualTo(64));
            Assert.That(typeof(ArtEditorState).GetField("TabletUrl"), Is.Null, "Capabilities must not enter the replicated BUI state");
            var intruder = entities.SpawnEntity("MobHuman", map.GridCoords);
            var attempt = new BoundUserInterfaceMessageAttempt(intruder, canvas, ArtUiKey.Key, new OpenBoundInterfaceMessage());
            entities.EventBus.RaiseLocalEvent(canvas, ref attempt);
            Assert.That(attempt.Cancelled, Is.True, "Another actor cannot subscribe to the editing interface");
            Assert.That(entities.GetComponent<ArtCanvasComponent>(canvas).Document, Is.EqualTo(ArtLimits.EmptyDocument), "Handoff must not commit or archive the draft");
        });
        var handle = typeof(ArtSystem).GetMethod("HandleTabletHttp", Private)!;
        async Task<(HttpStatusCode Status, JsonElement Body)> Request(string action, string token = "", string body = "", int revision = -1, string origin = "http://localhost", HttpMethod method = null,
            string writer = "", int sequence = 0, int epoch = 0, int version = 0)
        {
            var status = HttpStatusCode.OK;
            var response = JsonSerializer.SerializeToElement(new { });
            var context = new Mock<IStatusHandlerContext>();
            context.SetupGet(c => c.Url).Returns(new Uri("http://localhost/art-tablet/" + action));
            context.SetupGet(c => c.RequestMethod).Returns(method ?? (action is "session" or "ping" ? HttpMethod.Get : HttpMethod.Post));
            context.SetupGet(c => c.RequestBody).Returns(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            context.SetupGet(c => c.RequestHeaders).Returns(new Dictionary<string, StringValues> {
                ["Authorization"] = token.Length == 0 ? "" : "Bearer " + token, ["Origin"] = origin, ["If-Match"] = revision.ToString(),
                ["X-Art-Draft-Writer"] = writer, ["X-Art-Draft-Sequence"] = sequence.ToString(),
                ["X-Art-Draft-Epoch"] = epoch.ToString(), ["X-Art-Draft-Version"] = version.ToString() });
            var headers = new Dictionary<string, string>();
            context.SetupGet(c => c.ResponseHeaders).Returns(headers);
            context.Setup(c => c.RespondErrorAsync(It.IsAny<HttpStatusCode>())).Returns((HttpStatusCode code) => { status = code; return Task.CompletedTask; });
            context.Setup(c => c.RespondJsonAsync(It.IsAny<object>(), It.IsAny<HttpStatusCode>())).Returns((object value, HttpStatusCode code) => {
                status = code; response = JsonSerializer.SerializeToElement(value); return Task.CompletedTask;
            });
            var task = (Task<bool>)handle.Invoke(system, new object[] { context.Object })!;
            for (var i = 0; i < 600 && !task.IsCompleted; i++) { await pair.RunTicksSync(5); await Task.Delay(2); }
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(await task, Is.True);
            Assert.That(headers["Cache-Control"], Is.EqualTo("no-store"));
            Assert.That(headers["Referrer-Policy"], Is.EqualTo("no-referrer"));
            Assert.That(headers.ContainsKey("Access-Control-Allow-Origin"), Is.False);
            return (status, response);
        }
        Assert.That((await Request("session")).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await Request("claim", new string('0', 64))).Status, Is.EqualTo(HttpStatusCode.Gone));
        Assert.That((await Request("claim", ticket, origin: "https://other.example")).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        var claim = await Request("claim", ticket);
        Assert.That(claim.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(claim.Body.GetProperty("data").GetProperty("document").GetString(), Is.EqualTo(ArtDocument.Bake(document).Json));
        var token = claim.Body.GetProperty("token").GetString()!;
        Assert.That(token, Is.Not.EqualTo(ticket));
        Assert.That((await Request("claim", ticket)).Status, Is.EqualTo(HttpStatusCode.Gone), "A ticket can only be exchanged once");
        Assert.That((await Request("session", ticket)).Status, Is.EqualTo(HttpStatusCode.Gone));
        var initial = claim.Body.GetProperty("data").GetProperty("revision").GetInt32();
        Assert.That((await Request("save", token, document, initial - 1)).Status, Is.EqualTo(HttpStatusCode.Conflict));
        await pair.Server.WaitAssertion(() =>
        {
            Message("save", document);
            Assert.That(entities.GetComponent<ArtCanvasComponent>(canvas).Document, Is.EqualTo(ArtLimits.EmptyDocument), "Native writes are rejected during browser ownership");
        });
        var writer = Guid.NewGuid().ToString("N");
        var draftEpoch = claim.Body.GetProperty("data").GetProperty("draftEpoch").GetInt32();
        var draftVersion = claim.Body.GetProperty("data").GetProperty("draftVersion").GetInt32();
        var browserDraft = document.Replace("Планшет", "Несохранённый планшет");
        var draftResponse = await Request("draft", token, browserDraft, initial, writer: writer, sequence: 1, epoch: draftEpoch, version: draftVersion);
        Assert.That(draftResponse.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(draftResponse.Body.GetProperty("ack").GetProperty("success").GetBoolean(), Is.True);
        Assert.That(draftResponse.Body.TryGetProperty("data", out _), Is.False, "Autosave acknowledgment must not resend all PNG layers");
        await pair.Server.WaitAssertion(() =>
        {
            var data = entities.GetComponent<ArtCanvasComponent>(canvas);
            Assert.That(data.DraftDocument, Is.EqualTo(ArtDocument.Bake(browserDraft).Json));
            Assert.That(data.Document, Is.EqualTo(ArtLimits.EmptyDocument));
        });
        Assert.That(await pair.Server.ResolveDependency<IServerDbManager>().GetArtworkPoolAsync(), Is.Empty);
        var discarded = await Request("discard", token, revision: initial);
        Assert.That(discarded.Body.GetProperty("data").GetProperty("hasDraft").GetBoolean(), Is.False);
        Assert.That(discarded.Body.GetProperty("data").GetProperty("document").GetString(), Is.EqualTo(ArtLimits.EmptyDocument));
        await pair.RunTicksSync(90);
        var replay = await Request("draft", token, browserDraft, initial, writer: writer, sequence: 2, epoch: draftEpoch, version: draftVersion);
        Assert.That(replay.Body.GetProperty("ack").GetProperty("success").GetBoolean(), Is.False);
        var saved = await Request("save", token, document, initial);
        Assert.That(saved.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(saved.Body.GetProperty("data").GetProperty("result").GetInt32(), Is.GreaterThan(0));
        Assert.That(await pair.Server.ResolveDependency<IServerDbManager>().GetArtworkPoolAsync(), Has.Count.EqualTo(1));
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(State().TabletRevision, Is.EqualTo(2), "Discard and explicit save both refresh the native editor");
            Assert.That(State().Document, Is.EqualTo(ArtDocument.Bake(document).Json));
        });
        Assert.That((await Request("save", token, document, initial)).Status, Is.EqualTo(HttpStatusCode.Conflict), "Old revisions cannot overwrite a newer save");
        Assert.That((await Request("session", token, method: HttpMethod.Post)).Status, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        await pair.Server.WaitPost(() => Message("tablet-revoke"));
        Assert.That((await Request("save", token, document, initial + 1)).Status, Is.EqualTo(HttpStatusCode.Gone));
        await pair.RunTicksSync(180);
        await pair.Server.WaitPost(() => Message("tablet", document));
        string secondTicket = "";
        for (var i = 0; i < 150 && secondTicket.Length == 0; i++)
        {
            await pair.RunTicksSync(5);
            await pair.Server.WaitPost(() => { if (State().TabletActive) secondTicket = Ticket(); });
        }
        var second = await Request("claim", secondTicket);
        var secondToken = second.Body.GetProperty("token").GetString()!;
        await pair.Server.WaitPost(() =>
        {
            var leases = (IDictionary)typeof(ArtSystem).GetField("_tabletLeases", Private)!.GetValue(system)!;
            var lease = leases.Values.Cast<object>().Single();
            lease.GetType().GetField("Expires")!.SetValue(lease, DateTime.UtcNow.AddSeconds(-1));
        });
        Assert.That((await Request("session", secondToken)).Status, Is.EqualTo(HttpStatusCode.Gone));
        await pair.Server.WaitPost(() => ui.CloseUis(canvas));
        await pair.CleanReturnAsync();
    }
}
