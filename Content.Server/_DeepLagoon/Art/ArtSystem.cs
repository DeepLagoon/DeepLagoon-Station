using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Crayon;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.Art;
using Content.Shared._DeepLagoon.Apartments;
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
    private sealed class Upload(int count, TimeSpan expires, string action)
    {
        public readonly int Count = count;
        public readonly string Action = action;
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
        InitializeTablet();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => { _round++; _tabletLeases.Clear(); _tabletUpdates.Clear(); _poolTask = null; _pool = null; _available.Clear(); _uploads.Clear(); _previewUploads.Clear(); _draftUploads.Clear(); _draftAt.Clear(); });
        SubscribeLocalEvent<ArtCanvasComponent, MapInitEvent>((uid, c, _) => { if (c.WorkId == Guid.Empty) c.WorkId = Guid.NewGuid(); });
        SubscribeLocalEvent<ArtCanvasComponent, InteractUsingEvent>(OnUseCanvas);
        SubscribeLocalEvent<ArtCanvasComponent, ArtEditorMessage>(OnMessage);
        SubscribeLocalEvent<ArtCanvasComponent, BoundUserInterfaceMessageAttempt>(OnUiAttempt);
        SubscribeLocalEvent<ArtCanvasComponent, BoundUIClosedEvent>((uid, c, ev) => { if (c.Artist == ev.Actor) Release(uid, c); });
        SubscribeLocalEvent<ArtCanvasComponent, ComponentShutdown>((uid, c, _) => { _tabletLeases.Remove(uid); _tabletUpdates.Remove(uid); _uploads.Remove(uid); _previewAt.Remove(uid); _saveAt.Remove(uid); _results.Remove(uid); _previewUploads.Remove(uid); _draftUploads.Remove(uid); _draftAt.Remove(uid); });
        SubscribeLocalEvent<ArtFrameComponent, ComponentInit>((uid, _, _) => _containers.EnsureContainer<ContainerSlot>(uid, "art-canvas"));
        SubscribeLocalEvent<ArtFrameComponent, InteractUsingEvent>(OnFrameInteract);
        SubscribeLocalEvent<ArtFrameComponent, GetVerbsEvent<AlternativeVerb>>(OnFrameVerbs);
        SubscribeLocalEvent<ArtFrameComponent, MapInitEvent>((uid, c, _) => { if (c.FillOnRoundStart) FillFrame(uid, c); });
        SubscribeLocalEvent<ArtFrameItemComponent, AfterInteractEvent>(OnInstallFrame);
        SubscribeLocalEvent<ApartmentPaintingFrameComponent, ContainerIsInsertingAttemptEvent>(OnApartmentCanvasInsert);
    }

    private string? ToolKind(EntityUid tool) => TryComp<ArtToolComponent>(tool, out var art) ? art.Kind
        : HasComp<CrayonComponent>(tool) ? "chalk" : null;

    private string ToolColor(EntityUid? tool)
    {
        if (tool is not { } uid) return "#202020";
        var color = TryComp<ArtToolComponent>(uid, out var art) ? art.Color
            : TryComp<CrayonComponent>(uid, out var crayon) ? crayon.Color : Color.FromHex("#202020");
        return color.ToHexNoAlpha();
    }

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
        if (c.RecoveryId == Guid.Empty) c.RecoveryId = Guid.NewGuid();
        if (_players.TryGetSessionByEntity(ev.User, out var editor) && c.RecoveryOwner != editor.UserId.UserId)
        { c.RecoveryOwner = editor.UserId.UserId; c.DraftEpoch++; }
        c.DraftLeaseGeneration++;
        c.Artist = ev.User; c.Tool = ev.Used;
        SetPreview(uid, CurrentPreview(c));
        // Reopening invalidates delayed messages from a previous window, even before the first save.
        c.Revision++;
        Publish(uid, c);
        _ui.TryOpenUi(uid, ArtUiKey.Key, ev.User);
    }

    private void OnUiAttempt(EntityUid uid, ArtCanvasComponent c, ref BoundUserInterfaceMessageAttempt ev)
    {
        if (ev.UiKey.Equals(ArtUiKey.Key) && !CanEdit(uid, c, ev.Actor)) ev.Cancel();
    }

    private void Release(EntityUid uid, ArtCanvasComponent c)
    {
        _tabletLeases.Remove(uid);
        c.Artist = null; c.Tool = null; _uploads.Remove(uid); _previewUploads.Remove(uid); _draftUploads.Remove(uid);
        SetPreview(uid, CurrentPreview(c));
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
            if (_tabletLeases.TryGetValue(uid, out var lease) && !TabletValid(lease)) EndTablet(uid, c);
            if (_uploads.TryGetValue(uid, out var upload) && upload.Expires < _timing.CurTime) _uploads.Remove(uid);
            if (_draftUploads.TryGetValue(uid, out var draft) && draft.Expires < _timing.CurTime) _draftUploads.Remove(uid);
        }
    }

    private async void OnMessage(EntityUid uid, ArtCanvasComponent c, ArtEditorMessage ev) => await HandleMessage(uid, c, ev);

    private async Task HandleMessage(EntityUid uid, ArtCanvasComponent c, ArtEditorMessage ev, bool browser = false)
    {
        if (!ev.UiKey.Equals(ArtUiKey.Key) || !CanEdit(uid, c, ev.Actor) || ev.Revision != c.Revision || ev.Text == null)
            return;
        if (ev.Action == "tablet-revoke") { EndTablet(uid, c); return; }
        if (_tabletLeases.ContainsKey(uid) != browser) return;
        if (ev.Action == "draft") { await OnDraftChunk(uid, c, ev); return; }
        if (_baking.Contains(uid)) return;
        try
        {
            if (ev.Action == "discard") { _uploads.Remove(uid); _previewUploads.Remove(uid); ClearDraft(uid, c); Publish(uid, c); SetPreview(uid, c.SavedPreview); return; }
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
            string json;
            if (browser)
            {
                if (ev.Action != "save" || ev.Text.Length > ArtLimits.MaxDocumentChars) return;
                if (_saveAt.GetValueOrDefault(uid) > _timing.CurTime) { Publish(uid, c, "dl-art-wait", -1); return; }
                _saveAt[uid] = _timing.CurTime + TimeSpan.FromSeconds(2);
                json = ev.Text;
            }
            else
            {
                if (ev.Action is not ("save" or "tablet") || ev.Text.Length > ArtLimits.ChunkChars || ev.Count < 1
                    || ev.Count > (ArtLimits.MaxDocumentChars + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars
                    || ev.Index < 0 || ev.Index >= ev.Count) return;
                if (ev.Index == 0)
                {
                    if (_saveAt.GetValueOrDefault(uid) > _timing.CurTime) { Publish(uid, c, "dl-art-wait", -1); return; }
                    _saveAt[uid] = _timing.CurTime + TimeSpan.FromSeconds(2);
                    _uploads[uid] = new Upload(ev.Count, _timing.CurTime + TimeSpan.FromMinutes(2), ev.Action);
                }
                if (!_uploads.TryGetValue(uid, out var upload) || upload.Count != ev.Count || upload.Action != ev.Action || upload.Next != ev.Index
                    || upload.Expires < _timing.CurTime || upload.Data.Length + ev.Text.Length > ArtLimits.MaxDocumentChars)
                { _uploads.Remove(uid); Publish(uid, c, "dl-art-upload-failed", -1); return; }
                upload.Data.Append(ev.Text); upload.Next++;
                if (upload.Next != upload.Count) return;
                _uploads.Remove(uid);
                json = upload.Data.ToString();
            }
            var round = _round;
            var tabletLease = browser ? _tabletLeases.GetValueOrDefault(uid) : null;
            _baking.Add(uid);
            var baked = await Task.Run(() => ArtDocument.Bake(json));
            if (round != _round || !Exists(uid) || !CanEdit(uid, c, ev.Actor) || c.Revision != ev.Revision) return;
            if (browser && (tabletLease == null || _tabletLeases.GetValueOrDefault(uid) != tabletLease || !TabletValid(tabletLease))) return;
            if (!_players.TryGetSessionByEntity(ev.Actor, out var session)) return;
            if (ev.Action == "tablet")
            {
                if (baked.Json != CurrentDocument(c)) StoreDraft(uid, c, baked, ev.Writer, ev.Sequence);
                StartTablet(uid, c, ev.Actor, session, baked.Json);
                return;
            }
            var workId = c.Author != Guid.Empty && c.Author != session.UserId.UserId ? Guid.NewGuid() : c.WorkId;
            var authorName = Name(ev.Actor);
            if (authorName.Length > 128) authorName = authorName[..128];
            var record = new ArchivedArtwork { Id = Guid.NewGuid(), WorkId = workId, RevisionHash = baked.Hash,
                Author = session.UserId.UserId, AuthorName = authorName, Title = baked.Title, Document = baked.Json,
                Preview = baked.Preview, CreatedAt = DateTime.UtcNow };
            if (_removedArt.Contains((workId, baked.Hash))) { Publish(uid, c, "dl-art-moderation-removed", -1); return; }
            ClearDraft(uid, c);
            c.Document = baked.Json; c.SavedPreview = baked.Preview; c.Title = baked.Title;
            c.RevisionHash = baked.HasInk ? baked.Hash : ""; c.Revision++; c.WorkId = workId;
            c.Author = record.Author; c.AuthorName = authorName;
            if (browser)
            {
                _tabletUpdates[uid] = _tabletUpdates.GetValueOrDefault(uid) + 1;
                tabletLease!.Revision = c.Revision;
                tabletLease.Document = c.Document;
                tabletLease.Expires = DateTime.UtcNow.AddMinutes(15);
            }
            SetPreview(uid, c.SavedPreview);
            var savedRevision = c.Revision;
            if (!baked.HasInk) { Publish(uid, c, "dl-art-canvas-saved", 1); return; }
            Guid id;
            try
            {
                id = await _db.ArchiveArtworkAsync(record);
                var archived = await _db.GetArtworkAsync(id);
                if (round != _round || !Exists(uid) || !CanEdit(uid, c, ev.Actor) || c.Revision != savedRevision) return;
                if (archived == null || _removedArt.Contains((workId, baked.Hash)))
                { ClearRemovedCanvas(uid, c); return; }
            }
            catch (Exception e)
            {
                Log.Error($"Art archive failed: {e.GetType().Name}");
                if (round == _round && Exists(uid) && c.Revision == savedRevision) Publish(uid, c, "dl-art-db-failed", 1);
                return;
            }
            if (_pool != null && !_pool.Contains(id)) _pool.Add(id);
            Publish(uid, c, "dl-art-saved", 1);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _uploads.Remove(uid); _previewUploads.Remove(uid);
            Log.Warning($"Rejected artwork from {ev.Actor}: {e.GetType().Name}");
            if (Exists(uid)) Publish(uid, c, "dl-art-invalid", -1);
        }
        finally { _baking.Remove(uid); }
    }

    private void Publish(EntityUid uid, ArtCanvasComponent c, string status = "", int result = 0)
    {
        _results[uid] = _results.GetValueOrDefault(uid) + 1;
        _ui.SetUiState(uid, ArtUiKey.Key, new ArtEditorState(CurrentDocument(c), c.Revision,
            c.Tool is { } tool ? ToolKind(tool) ?? "brush" : "brush", status, result == 0 ? 0 : _results[uid] * result, ToolColor(c.Tool))
        {
            SavedDocument = c.Document, HasDraft = c.DraftDocument.Length != 0,
            RecoveryKey = c.RecoveryId.ToString("N") + ":" + c.RecoveryOwner.ToString("N"),
            DraftEpoch = c.DraftEpoch, DraftVersion = c.DraftVersion, DraftWriter = c.DraftWriter, DraftSequence = c.DraftSequence,
            TabletAvailable = TabletBaseUrl() != null,
            TabletActive = _tabletLeases.ContainsKey(uid),
            TabletRevision = _tabletUpdates.GetValueOrDefault(uid),
        });
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
        var apartments = EntityManager.System<ApartmentSystem>();
        var privateFrame = HasComp<ApartmentPaintingFrameComponent>(uid) || HasComp<ApartmentGridComponent>(Transform(wall).GridUid);
        if (privateFrame && !apartments.CanDecorate(ev.User, wall))
        {
            ev.Handled = true;
            _popup.PopupEntity(Loc.GetString("dl-art-apartment-owner-only"), uid, ev.User);
            return;
        }
        var coords = Transform(wall).Coordinates;
        var frames = EntityQueryEnumerator<ArtFrameComponent, TransformComponent>();
        while (frames.MoveNext(out _, out _, out var xform))
            if (xform.Coordinates.EntityId == coords.EntityId && (xform.Coordinates.Position - coords.Position).LengthSquared() < .01f) return;
        var frame = Spawn(privateFrame ? "DLApartmentPaintingFrame" : "DLArtFrame", coords);
        // The prototype may already have anchored during spawn; never add it to the snap grid twice.
        if (!Transform(frame).Anchored && !_transform.AnchorEntity(frame)) { QueueDel(frame); return; }
        ev.Handled = true;
        if (privateFrame)
        {
            if (!_players.TryGetSessionByEntity(ev.User, out var owner)) { Del(frame); return; }
            Comp<ApartmentPaintingFrameComponent>(frame).Owner = owner.UserId.UserId;
            // The held item is excluded with the visitor from the snapshot. Commit
            // the mounted frame before consuming it, so failed writes keep the item.
            if (!apartments.SaveDecoration(frame))
            {
                Del(frame);
                _popup.PopupEntity(Loc.GetString("dl-art-apartment-save-failed"), uid, ev.User);
                return;
            }
            QueueDel(uid);
            return;
        }
        if (!_hands.TryDrop(ev.User, uid)) { QueueDel(frame); return; }
        QueueDel(uid);
    }
    private void OnFrameInteract(EntityUid uid, ArtFrameComponent c, InteractUsingEvent ev)
    {
        if (ev.Handled || c.Busy || !Transform(uid).Anchored || !TryComp<ArtCanvasComponent>(ev.Used, out var canvas)
            || !_hands.IsHolding(ev.User, ev.Used, out _) || !_interaction.InRangeUnobstructed(ev.User, uid) || Slot(uid).ContainedEntity != null) return;
        ev.Handled = true;
        var privateFrame = HasComp<ApartmentPaintingFrameComponent>(uid);
        if (privateFrame && !CanChangeApartmentFrame(uid, ev.User))
        { _popup.PopupEntity(Loc.GetString("dl-art-apartment-owner-only"), uid, ev.User); return; }
        if (privateFrame && !HasComp<ArtDurableCanvasComponent>(ev.Used))
        { _popup.PopupEntity(Loc.GetString("dl-art-durable-only"), uid, ev.User); return; }
        if (canvas.Artist != null) { _popup.PopupEntity(Loc.GetString("dl-art-close-first"), uid, ev.User); return; }
        if (privateFrame ? CurrentPreview(canvas).Length == 0 : canvas.RevisionHash.Length == 0)
        { _popup.PopupEntity(Loc.GetString("dl-art-empty"), uid, ev.User); return; }
        if (!_hands.TryDropIntoContainer(ev.User, ev.Used, Slot(uid))) return;
        SetPreview(uid, privateFrame ? CurrentPreview(canvas) : canvas.SavedPreview);
        if (privateFrame && !EntityManager.System<ApartmentSystem>().SaveDecoration(uid))
        {
            _containers.Remove(ev.Used, Slot(uid));
            _hands.TryPickupAnyHand(ev.User, ev.Used);
            SetPreview(uid, "");
            _popup.PopupEntity(Loc.GetString("dl-art-apartment-save-failed"), uid, ev.User);
        }
    }

    private void OnFrameVerbs(EntityUid uid, ArtFrameComponent c, GetVerbsEvent<AlternativeVerb> ev)
    {
        if (!ev.CanAccess || !ev.CanInteract || c.Busy || Slot(uid).ContainedEntity is not { } canvas ||
            HasComp<ApartmentPaintingFrameComponent>(uid) && !CanChangeApartmentFrame(uid, ev.User)) return;
        var user = ev.User;
        ev.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-art-remove"), Act = () =>
        {
            if (!Exists(uid) || c.Busy || Slot(uid).ContainedEntity != canvas || !_interaction.InRangeUnobstructed(user, uid)) return;
            if (HasComp<ApartmentPaintingFrameComponent>(uid) && !CanChangeApartmentFrame(uid, user)) return;
            if (!_containers.Remove(canvas, Slot(uid))) return;
            SetPreview(uid, "");
            // Save the removed canvas on the floor before handing it over, rather
            // than excluding it with a visitor's inventory from this checkpoint.
            if (HasComp<ApartmentPaintingFrameComponent>(uid) && !EntityManager.System<ApartmentSystem>().SaveDecoration(uid))
            {
                _containers.Insert(canvas, Slot(uid));
                SetPreview(uid, CurrentPreview(Comp<ArtCanvasComponent>(canvas)));
                _popup.PopupEntity(Loc.GetString("dl-art-apartment-save-failed"), uid, user);
                return;
            }
            _hands.TryPickupAnyHand(user, canvas);
        }});
    }

    private bool CanChangeApartmentFrame(EntityUid uid, EntityUid user) =>
        EntityManager.System<ApartmentSystem>().CanDecorate(user, uid) &&
        _players.TryGetSessionByEntity(user, out var session) && Comp<ApartmentPaintingFrameComponent>(uid).Owner == session.UserId.UserId;

    private void OnApartmentCanvasInsert(EntityUid uid, ApartmentPaintingFrameComponent component, ContainerIsInsertingAttemptEvent ev)
    {
        if (ev.Container.ID == "art-canvas" && (!HasComp<ArtDurableCanvasComponent>(ev.EntityUid) || !HasComp<ArtCanvasComponent>(ev.EntityUid)))
            ev.Cancel();
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
            if (work == null || _removedArt.Contains((work.WorkId, work.RevisionHash)) || round != _round || !Exists(uid) || Slot(uid).ContainedEntity != null) return;
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
