using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared._DeepLagoon.Art;
using Robust.Server.ServerStatus;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Art;

public sealed partial class ArtSystem
{
    [Dependency] private IStatusHost _tabletHost = default!;
    [Dependency] private ITaskManager _tabletTasks = default!;
    [Dependency] private IConfigurationManager _tabletConfig = default!;
    [Dependency] private IResourceManager _tabletResources = default!;
    private const string TabletRoute = "/art-tablet/";
    private readonly Dictionary<EntityUid, TabletLease> _tabletLeases = new();
    private readonly Dictionary<EntityUid, int> _tabletUpdates = new();
    private readonly SemaphoreSlim _tabletHttpSlots = new(4, 4);
    private sealed class TabletLease
    {
        public required EntityUid Canvas;
        public required EntityUid Actor;
        public required ICommonSession Session;
        public required int Round;
        public required int Revision;
        public required string Document;
        public required string Ticket;
        public required string Token;
        public required string Url;
        public bool Claimed;
        public bool DraftChanged;
        public readonly DateTime Created = DateTime.UtcNow;
        public DateTime Expires = DateTime.UtcNow.AddMinutes(2);
    }

    private void InitializeTablet() => _tabletHost.AddHandler(HandleTabletHttp);

    private Uri? TabletBaseUrl()
    {
        var configured = _tabletConfig.GetCVar(ArtCVars.TabletUrl);
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.AbsolutePath != TabletRoute || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            return null;
        return uri;
    }

    private static string NewTabletToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private void StartTablet(EntityUid uid, ArtCanvasComponent c, EntityUid actor, ICommonSession session, string document)
    {
        var address = TabletBaseUrl();
        if (address == null) { Publish(uid, c, "dl-art-tablet-unavailable", -1); return; }
        c.DraftLeaseGeneration++;
        var ticket = NewTabletToken();
        _tabletLeases[uid] = new TabletLease
        {
            Canvas = uid, Actor = actor, Session = session, Round = _round, Revision = c.Revision,
            Document = document, Ticket = ticket, Token = NewTabletToken(), Url = address.AbsoluteUri + "#" + ticket,
        };
        _saveAt.Remove(uid);
        Publish(uid, c, "dl-art-tablet-opened", 1);
        _ui.ServerSendUiMessage(uid, ArtUiKey.Key, new ArtTabletLinkMessage(_tabletLeases[uid].Url), actor);
    }

    private bool TabletValid(TabletLease lease) => lease.Round == _round && DateTime.UtcNow < lease.Expires &&
        DateTime.UtcNow < lease.Created.AddHours(2) && Exists(lease.Canvas) && Exists(lease.Actor) &&
        TryComp<ArtCanvasComponent>(lease.Canvas, out var c) && c.Revision == lease.Revision && CanEdit(lease.Canvas, c, lease.Actor) &&
        _players.TryGetSessionByEntity(lease.Actor, out var player) && player.Channel.IsConnected && ReferenceEquals(player, lease.Session);

    private void EndTablet(EntityUid uid, ArtCanvasComponent c)
    {
        if (!_tabletLeases.Remove(uid, out var lease)) return;
        c.DraftLeaseGeneration++;
        if (lease.DraftChanged) _tabletUpdates[uid] = _tabletUpdates.GetValueOrDefault(uid) + 1;
        Publish(uid, c);
    }

    private object TabletData(TabletLease lease, ArtCanvasComponent c, string status = "", int result = 0)
    {
        var labels = new Dictionary<string, string>();
        foreach (var key in ArtTabletLabels.Keys) labels[key] = Loc.GetString("dl-art-ui-" + key);
        return new { document = CurrentDocument(c), savedDocument = c.Document, hasDraft = c.DraftDocument.Length != 0,
            recoveryKey = c.RecoveryId.ToString("N") + ":" + c.RecoveryOwner.ToString("N"),
            draftEpoch = c.DraftEpoch, draftVersion = c.DraftVersion, draftWriter = c.DraftWriter, draftSequence = c.DraftSequence,
            revision = c.Revision, tool = c.Tool is { } tool ? ToolKind(tool) : "brush",
            color = ToolColor(c.Tool), status = status.Length == 0 ? "" : Loc.GetString(status), result, labels,
            maxLayers = ArtLimits.MaxLayers, maxDocument = ArtLimits.MaxDocumentChars, chunkSize = ArtLimits.ChunkChars, external = true };
    }

    private Task<T> TabletOnMain<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tabletTasks.RunOnMainThread(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception e) { completion.TrySetException(e); }
        });
        return completion.Task;
    }

    private async Task<bool> HandleTabletHttp(IStatusHandlerContext context)
    {
        if (!context.Url.AbsolutePath.StartsWith(TabletRoute, StringComparison.Ordinal)) return false;
        context.ResponseHeaders["Cache-Control"] = "no-store";
        context.ResponseHeaders["Referrer-Policy"] = "no-referrer";
        context.ResponseHeaders["X-Content-Type-Options"] = "nosniff";
        context.ResponseHeaders["X-Frame-Options"] = "DENY";
        context.ResponseHeaders["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
        var path = context.Url.AbsolutePath[TabletRoute.Length..];
        var file = path switch
        {
            "" => ("tablet.html", "text/html; charset=utf-8"),
            "tgui.bundle.js" => (path, "application/javascript"),
            "tgui.bundle.css" => (path, "text/css"),
            "font-awesome/css/all.min.css" => (path, "text/css"),
            "font-awesome/webfonts/fa-solid-900.woff2" or "font-awesome/webfonts/fa-regular-400.woff2" => (path, "font/woff2"),
            _ => ("", ""),
        };
        if (context.RequestMethod == HttpMethod.Get && file.Item1.Length != 0)
        {
            if (_tabletResources.TryContentFileRead("/Web/DeepLagoon/" + file.Item1, out var stream))
            {
                using (stream)
                using (var bytes = new MemoryStream())
                {
                    await stream.CopyToAsync(bytes);
                    await context.RespondAsync(bytes.ToArray(), HttpStatusCode.OK, file.Item2);
                }
            }
            else await context.RespondErrorAsync(HttpStatusCode.NotFound);
            return true;
        }
        if (path is not ("claim" or "session" or "ping" or "save" or "draft" or "discard" or "release"))
        { await context.RespondErrorAsync(HttpStatusCode.NotFound); return true; }
        if (context.RequestMethod != (path is "session" or "ping" ? HttpMethod.Get : HttpMethod.Post))
        { await context.RespondErrorAsync(HttpStatusCode.MethodNotAllowed); return true; }
        var origin = context.RequestHeaders.TryGetValue("Origin", out var origins) ? origins.ToString() : "";
        var allowedOrigin = await TabletOnMain(() => Task.FromResult(TabletBaseUrl()?.GetLeftPart(UriPartial.Authority)));
        if (allowedOrigin == null || (origin.Length != 0 && origin != allowedOrigin))
        { await context.RespondErrorAsync(HttpStatusCode.Forbidden); return true; }
        var authorization = context.RequestHeaders.TryGetValue("Authorization", out var auth) ? auth.ToString() : "";
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length != 71)
        { await context.RespondErrorAsync(HttpStatusCode.Unauthorized); return true; }
        var token = authorization[7..];
        if (!_tabletHttpSlots.Wait(0))
        { await context.RespondErrorAsync(HttpStatusCode.TooManyRequests); return true; }
        try
        {
            var authorized = await TabletOnMain(() => Task.FromResult(FindTablet(token, path == "claim") != null));
            if (!authorized) { await context.RespondErrorAsync(HttpStatusCode.Gone); return true; }
            var document = "";
            if (path is "save" or "draft")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var body = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await context.RequestBody.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (body.Length + count > ArtLimits.MaxDocumentChars + 8192)
                    { await context.RespondErrorAsync(HttpStatusCode.RequestEntityTooLarge); return true; }
                    body.Write(buffer, 0, count);
                }
                document = new UTF8Encoding(false, true).GetString(body.GetBuffer(), 0, (int)body.Length);
            }
            var expected = context.RequestHeaders.TryGetValue("If-Match", out var revision) && int.TryParse(revision.ToString(), out var parsed) ? parsed : -1;
            string Header(string key) => context.RequestHeaders.TryGetValue(key, out var value) ? value.ToString() : "";
            int Number(string key) => int.TryParse(Header(key), out var value) ? value : -1;
            var writer = Header("X-Art-Draft-Writer");
            if (writer.Length > 32) { await context.RespondErrorAsync(HttpStatusCode.BadRequest); return true; }
            var reply = await TabletOnMain(() => TabletRequest(token, path, document, expected,
                writer, Number("X-Art-Draft-Sequence"), Number("X-Art-Draft-Epoch"), Number("X-Art-Draft-Version")));
            await context.RespondJsonAsync(reply.Body, reply.Status);
        }
        catch (Exception e) when (e is OperationCanceledException or DecoderFallbackException)
        { await context.RespondErrorAsync(HttpStatusCode.BadRequest); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"Art tablet request failed: {e.GetType().Name}");
            await context.RespondErrorAsync(HttpStatusCode.ServiceUnavailable);
        }
        finally { _tabletHttpSlots.Release(); }
        return true;
    }

    private TabletLease? FindTablet(string token, bool ticket) => _tabletLeases.Values.FirstOrDefault(lease =>
        (ticket ? !lease.Claimed && lease.Ticket == token : lease.Claimed && lease.Token == token) && TabletValid(lease));

    private sealed record TabletReply(HttpStatusCode Status, object Body);
    private async Task<TabletReply> TabletRequest(string token, string action, string document, int revision, string writer, int sequence, int epoch, int version)
    {
        var lease = FindTablet(token, action == "claim");
        if (lease == null) return new(HttpStatusCode.Gone, new { error = "expired" });
        var c = Comp<ArtCanvasComponent>(lease.Canvas);
        lease.Expires = DateTime.UtcNow.AddMinutes(15);
        if (action == "ping") return new(HttpStatusCode.OK, new { active = true, revision = c.Revision });
        if (action == "claim")
        {
            lease.Claimed = true; lease.Expires = DateTime.UtcNow.AddMinutes(15);
            Publish(lease.Canvas, c);
            return new(HttpStatusCode.OK, new { token = lease.Token, data = TabletData(lease, c) });
        }
        if (action == "release")
        {
            if (_baking.Contains(lease.Canvas)) return new(HttpStatusCode.Conflict, new { error = "busy" });
            EndTablet(lease.Canvas, c);
            return new(HttpStatusCode.OK, new { closed = true });
        }
        if (action == "discard")
        {
            if (_baking.Contains(lease.Canvas)) return new(HttpStatusCode.Conflict, new { error = "busy" });
            ClearDraft(lease.Canvas, c); lease.Document = c.Document;
            _tabletUpdates[lease.Canvas] = _tabletUpdates.GetValueOrDefault(lease.Canvas) + 1;
            Publish(lease.Canvas, c); SetPreview(lease.Canvas, c.SavedPreview);
            return new(HttpStatusCode.OK, new { data = TabletData(lease, c) });
        }
        if (action == "draft")
        {
            if (revision != c.Revision) return new(HttpStatusCode.Conflict, new { error = "revision" });
            if (_draftAt.GetValueOrDefault(lease.Canvas) > _timing.CurTime) return new(HttpStatusCode.TooManyRequests, new { error = "busy" });
            _draftAt[lease.Canvas] = _timing.CurTime + TimeSpan.FromSeconds(1);
            var message = new ArtEditorMessage("draft", document, revision, writer: writer, sequence: sequence, epoch: epoch, version: version)
                { Actor = lease.Actor, UiKey = ArtUiKey.Key };
            var ack = await BakeDraft(lease.Canvas, c, message, document);
            return new(HttpStatusCode.OK, new { ack = new { writer = ack.Writer, sequence = ack.Sequence, epoch = ack.Epoch, version = ack.Version, success = ack.Success } });
        }
        if (action == "save")
        {
            if (revision != c.Revision) return new(HttpStatusCode.Conflict, new { error = "revision" });
            if (_baking.Contains(lease.Canvas) || _saveAt.GetValueOrDefault(lease.Canvas) > _timing.CurTime)
                return new(HttpStatusCode.TooManyRequests, new { error = "busy" });
            await HandleMessage(lease.Canvas, c, new ArtEditorMessage("save", document, revision)
                { Actor = lease.Actor, UiKey = ArtUiKey.Key }, browser: true);
            if (!_tabletLeases.TryGetValue(lease.Canvas, out var current) || current != lease || !TabletValid(lease))
                return new(HttpStatusCode.Gone, new { error = "expired" });
            if (!_ui.TryGetUiState<ArtEditorState>(lease.Canvas, ArtUiKey.Key, out var state))
                return new(HttpStatusCode.Gone, new { error = "expired" });
            return new(HttpStatusCode.OK, new { data = TabletData(lease, c, state.Status, state.Result) });
        }
        return new(HttpStatusCode.OK, new { data = TabletData(lease, c) });
    }
}
