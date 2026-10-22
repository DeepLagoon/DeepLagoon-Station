using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Crayon;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.Art;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.GameTicking;
using Content.Shared.Verbs;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Art;

public sealed partial class ArtSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    private sealed class Upload(int count, TimeSpan expires)
    {
        public readonly int Count = count;
        public readonly TimeSpan Expires = expires;
        public int Next;
        public readonly StringBuilder Data = new();
    }
    private readonly Dictionary<EntityUid, Upload> _uploads = new();
    private readonly Dictionary<EntityUid, TimeSpan> _previewAt = new();
    private readonly Dictionary<EntityUid, TimeSpan> _saveAt = new();
    private readonly Dictionary<EntityUid, int> _results = new();
    private readonly Dictionary<EntityUid, string> _previewUploads = new();
    private Task<List<Guid>>? _poolTask;
    private List<Guid>? _pool;
    private int _round;
    private readonly HashSet<EntityUid> _baking = new();
    private readonly List<Guid> _available = new();
    private float _checkTimer;

    public override void Initialize()
    {
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => { _round++; _poolTask = null; _pool = null; _available.Clear(); _uploads.Clear(); _previewUploads.Clear(); });
        SubscribeLocalEvent<ArtCanvasComponent, ComponentInit>((uid, c, _) => { if (c.WorkId == Guid.Empty) c.WorkId = Guid.NewGuid(); });
        SubscribeLocalEvent<ArtCanvasComponent, InteractUsingEvent>(OnUseCanvas);
        SubscribeLocalEvent<ArtCanvasComponent, ArtEditorMessage>(OnMessage);
        SubscribeLocalEvent<ArtCanvasComponent, BoundUIClosedEvent>((uid, c, ev) => { if (c.Artist == ev.Actor) Release(uid, c); });
        SubscribeLocalEvent<ArtCanvasComponent, ComponentShutdown>((uid, c, _) => { _uploads.Remove(uid); _previewAt.Remove(uid); _saveAt.Remove(uid); _results.Remove(uid); _previewUploads.Remove(uid); });
        SubscribeLocalEvent<ArtFrameComponent, ComponentInit>((uid, _, _) => _containers.EnsureContainer<ContainerSlot>(uid, "art-canvas"));
        SubscribeLocalEvent<ArtFrameComponent, InteractUsingEvent>(OnFrameInteract);
        SubscribeLocalEvent<ArtFrameComponent, GetVerbsEvent<AlternativeVerb>>(OnFrameVerbs);
        SubscribeLocalEvent<ArtFrameComponent, MapInitEvent>((uid, c, _) => { if (c.FillOnRoundStart) FillFrame(uid, c); });
        SubscribeLocalEvent<ArtFrameItemComponent, AfterInteractEvent>(OnInstallFrame);
    }

    private string? ToolKind(EntityUid tool) => TryComp<ArtToolComponent>(tool, out var art) ? art.Kind
        : HasComp<CrayonComponent>(tool) ? "chalk" : null;

    private bool InFrame(EntityUid uid) => _containers.TryGetContainingContainer(uid, out var container) && HasComp<ArtFrameComponent>(container.Owner);
    private bool CanEdit(EntityUid uid, ArtCanvasComponent c, EntityUid user) => c.Artist == user && c.Tool is { } tool
        && Exists(tool) && ToolKind(tool) != null && _hands.IsHolding(user, tool, out _)
        && _interaction.InRangeUnobstructed(user, uid) && !InFrame(uid);

    private void OnUseCanvas(EntityUid uid, ArtCanvasComponent c, InteractUsingEvent ev)
    {
        if (ev.Handled || ToolKind(ev.Used) is not { } kind || !_hands.IsHolding(ev.User, ev.Used, out _)
            || !_interaction.InRangeUnobstructed(ev.User, uid) || InFrame(uid)) return;
        ev.Handled = true;
        if (c.Artist is { } artist && CanEdit(uid, c, artist))
        {
            _popup.PopupEntity(Loc.GetString("dl-art-in-use"), uid, ev.User);
            return;
        }
        // One editor lease per actor. Opening another canvas rolls back its transient preview.
        var canvases = EntityQueryEnumerator<ArtCanvasComponent>();
        while (canvases.MoveNext(out var other, out var otherCanvas))
            if (otherCanvas.Artist == ev.User) { _ui.CloseUis(other); Release(other, otherCanvas); }
        c.Artist = ev.User; c.Tool = ev.Used;
        // Reopening invalidates delayed messages from a previous window, even before the first save.
        c.Revision++;
        Publish(uid, c);
        _ui.TryOpenUi(uid, ArtUiKey.Key, ev.User);
    }

    private void Release(EntityUid uid, ArtCanvasComponent c)
    {
        c.Artist = null; c.Tool = null; _uploads.Remove(uid); _previewUploads.Remove(uid);
        SetPreview(uid, c.SavedPreview);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _checkTimer += frameTime;
        if (_checkTimer < .5f) return;
        _checkTimer = 0;
        var query = EntityQueryEnumerator<ArtCanvasComponent>();
        while (query.MoveNext(out var uid, out var c))
        {
            if (c.Artist is { } user && (!Exists(user) || !CanEdit(uid, c, user))) { _ui.CloseUis(uid); Release(uid, c); }
            if (_uploads.TryGetValue(uid, out var upload) && upload.Expires < _timing.CurTime) _uploads.Remove(uid);
        }
    }

    private async void OnMessage(EntityUid uid, ArtCanvasComponent c, ArtEditorMessage ev)
    {
        if (!ev.UiKey.Equals(ArtUiKey.Key) || !CanEdit(uid, c, ev.Actor) || ev.Revision != c.Revision || ev.Text == null)
            return;
        if (_baking.Contains(uid)) return;
        try
        {
            if (ev.Action == "discard") { _uploads.Remove(uid); _previewUploads.Remove(uid); SetPreview(uid, c.SavedPreview); return; }
            // Thumbnail fragments fit the stock TGUI 8192-character action limit.
            if (ev.Action == "preview")
            {
                if (ev.Text.Length > ArtLimits.ChunkChars || ev.Count is < 1 or > 21 || ev.Index < 0 || ev.Index >= ev.Count) return;
                if (ev.Index == 0) _previewUploads[uid] = "";
                if (!_previewUploads.TryGetValue(uid, out var text)) return;
                if (text.Length != ev.Index * ArtLimits.ChunkChars) { _previewUploads.Remove(uid); return; }
                text += ev.Text;
                if (text.Length > ArtLimits.MaxPreviewChars) { _previewUploads.Remove(uid); return; }
                _previewUploads[uid] = text;
                if (ev.Index + 1 != ev.Count) return;
                _previewUploads.Remove(uid);
                if (_previewAt.GetValueOrDefault(uid) > _timing.CurTime) return;
                _previewAt[uid] = _timing.CurTime + TimeSpan.FromMilliseconds(400);
                SetPreview(uid, ArtDocument.NormalizePreview(text));
                return;
            }
            if (ev.Action != "save" || ev.Text.Length > ArtLimits.ChunkChars || ev.Count < 1
                || ev.Count > (ArtLimits.MaxDocumentChars + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars
                || ev.Index < 0 || ev.Index >= ev.Count) return;
            if (ev.Index == 0)
            {
                if (_saveAt.GetValueOrDefault(uid) > _timing.CurTime) { Publish(uid, c, "dl-art-wait", -1); return; }
                _saveAt[uid] = _timing.CurTime + TimeSpan.FromSeconds(2);
                _uploads[uid] = new Upload(ev.Count, _timing.CurTime + TimeSpan.FromMinutes(2));
            }
            if (!_uploads.TryGetValue(uid, out var upload) || upload.Count != ev.Count || upload.Next != ev.Index
                || upload.Expires < _timing.CurTime || upload.Data.Length + ev.Text.Length > ArtLimits.MaxDocumentChars)
            { _uploads.Remove(uid); Publish(uid, c, "dl-art-upload-failed", -1); return; }
            upload.Data.Append(ev.Text); upload.Next++;
            if (upload.Next != upload.Count) return;
            _uploads.Remove(uid);
            var json = upload.Data.ToString();
            ArtDocument.Baked baked;
            _baking.Add(uid);
            try { baked = await Task.Run(() => ArtDocument.Bake(json)); }
            finally { _baking.Remove(uid); }
            if (!Exists(uid) || !CanEdit(uid, c, ev.Actor) || c.Revision != ev.Revision) return;
            c.Document = baked.Json; c.SavedPreview = baked.Preview; c.Title = baked.Title;
            c.RevisionHash = baked.HasInk ? baked.Hash : ""; c.Revision++;
            if (baked.HasInk && _players.TryGetSessionByEntity(ev.Actor, out var session))
            {
                // New edits of a displayed copy become a new work; reframing unchanged copies is idempotent.
                if (c.Author != Guid.Empty && c.Author != session.UserId.UserId) c.WorkId = Guid.NewGuid();
                c.Author = session.UserId.UserId; c.AuthorName = Name(ev.Actor);
                if (c.AuthorName.Length > 128) c.AuthorName = c.AuthorName[..128];
            }
            SetPreview(uid, c.SavedPreview);
            Publish(uid, c, "dl-art-saved", 1);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _uploads.Remove(uid); _previewUploads.Remove(uid);
            Log.Warning($"Rejected artwork from {ev.Actor}: {e.GetType().Name}");
            if (Exists(uid)) Publish(uid, c, "dl-art-invalid", -1);
        }
    }

    private void Publish(EntityUid uid, ArtCanvasComponent c, string status = "", int result = 0)
    {
        _results[uid] = _results.GetValueOrDefault(uid) + 1;
        _ui.SetUiState(uid, ArtUiKey.Key, new ArtEditorState(c.Document, c.Revision,
            c.Tool is { } tool ? ToolKind(tool) ?? "brush" : "brush", status, result == 0 ? 0 : _results[uid] * result));
    }
    private void SetPreview(EntityUid uid, string preview)
    {
        if (!TryComp<ArtDisplayComponent>(uid, out var display) || display.Preview == preview) return;
        display.Preview = preview; Dirty(uid, display);
    }

    private ContainerSlot Slot(EntityUid uid) => _containers.EnsureContainer<ContainerSlot>(uid, "art-canvas");
    private void OnInstallFrame(EntityUid uid, ArtFrameItemComponent c, AfterInteractEvent ev)
    {
        if (ev.Handled || !ev.CanReach || ev.Target is not { } wall || !_tags.HasTag(wall, "Wall")
            || !_hands.IsHolding(ev.User, uid, out _) || Transform(wall).GridUid == null) return;
        var coords = Transform(wall).Coordinates;
        var frames = EntityQueryEnumerator<ArtFrameComponent, TransformComponent>();
        while (frames.MoveNext(out _, out _, out var xform))
            if (xform.Coordinates.EntityId == coords.EntityId && (xform.Coordinates.Position - coords.Position).LengthSquared() < .01f) return;
        var frame = Spawn("DLArtFrame", coords);
        // The prototype may already have anchored during spawn; never add it to the snap grid twice.
        if (!Transform(frame).Anchored && !_transform.AnchorEntity(frame)) { QueueDel(frame); return; }
        ev.Handled = true;
        if (!_hands.TryDrop(ev.User, uid)) { QueueDel(frame); return; }
        QueueDel(uid);
    }
    private async void OnFrameInteract(EntityUid uid, ArtFrameComponent c, InteractUsingEvent ev)
    {
        if (ev.Handled || c.Busy || !Transform(uid).Anchored || !TryComp<ArtCanvasComponent>(ev.Used, out var canvas)
            || !_hands.IsHolding(ev.User, ev.Used, out _) || !_interaction.InRangeUnobstructed(ev.User, uid) || Slot(uid).ContainedEntity != null) return;
        ev.Handled = true;
        if (canvas.Artist != null) { _popup.PopupEntity(Loc.GetString("dl-art-close-first"), uid, ev.User); return; }
        if (canvas.RevisionHash.Length == 0) { _popup.PopupEntity(Loc.GetString("dl-art-empty"), uid, ev.User); return; }
        if (!_hands.TryDropIntoContainer(ev.User, ev.Used, Slot(uid))) return;
        SetPreview(uid, canvas.SavedPreview);
        c.Busy = true;
        var record = new ArchivedArtwork { Id = Guid.NewGuid(), WorkId = canvas.WorkId, RevisionHash = canvas.RevisionHash,
            Author = canvas.Author, AuthorName = canvas.AuthorName, Title = canvas.Title, Document = canvas.Document,
            Preview = canvas.SavedPreview, CreatedAt = DateTime.UtcNow };
        try
        {
            var id = await _db.ArchiveArtworkAsync(record);
            if (_pool != null && !_pool.Contains(id)) _pool.Add(id);
            if (Exists(uid) && Exists(ev.User)) _popup.PopupEntity(Loc.GetString("dl-art-archived"), uid, ev.User);
        }
        catch (Exception e)
        {
            Log.Error($"Art archive failed: {e.GetType().Name}");
            if (Exists(uid) && Exists(ev.User)) _popup.PopupEntity(Loc.GetString("dl-art-db-failed"), uid, ev.User);
        }
        finally { if (Exists(uid)) c.Busy = false; }
    }

    private void OnFrameVerbs(EntityUid uid, ArtFrameComponent c, GetVerbsEvent<AlternativeVerb> ev)
    {
        if (!ev.CanAccess || !ev.CanInteract || c.Busy || Slot(uid).ContainedEntity is not { } canvas) return;
        var user = ev.User;
        ev.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-art-remove"), Act = () =>
        {
            if (!Exists(uid) || c.Busy || Slot(uid).ContainedEntity != canvas || !_interaction.InRangeUnobstructed(user, uid)) return;
            if (!_containers.Remove(canvas, Slot(uid))) return;
            SetPreview(uid, ""); _hands.TryPickupAnyHand(user, canvas);
        }});
    }

    private async void FillFrame(EntityUid uid, ArtFrameComponent c)
    {
        c.Busy = true;
        var round = _round;
        try
        {
            _poolTask ??= _db.GetArtworkPoolAsync();
            List<Guid> pool;
            try { pool = await _poolTask; } catch { _poolTask = null; throw; }
            if (round != _round || !Exists(uid)) return;
            _pool = pool;
            if (Slot(uid).ContainedEntity != null || pool.Count == 0) return;
            if (_available.Count == 0) _available.AddRange(pool);
            var index = _random.Next(_available.Count); var id = _available[index]; _available.RemoveAt(index);
            var work = await _db.GetArtworkAsync(id);
            if (work == null || round != _round || !Exists(uid) || Slot(uid).ContainedEntity != null) return;
            var canvas = Spawn("DLArtCanvas", Transform(uid).Coordinates);
            var data = Comp<ArtCanvasComponent>(canvas);
            data.Document = work.Document; data.SavedPreview = work.Preview; data.Title = work.Title;
            data.WorkId = work.WorkId; data.RevisionHash = work.RevisionHash; data.Author = work.Author; data.AuthorName = work.AuthorName;
            SetPreview(canvas, work.Preview);
            if (!_containers.Insert(canvas, Slot(uid))) { QueueDel(canvas); return; }
            SetPreview(uid, work.Preview);
        }
        catch (Exception e) { Log.Error($"Art gallery load failed: {e.GetType().Name}"); }
        finally { if (Exists(uid)) c.Busy = false; }
    }
}
