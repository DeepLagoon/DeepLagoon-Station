using System.IO;
using System.Linq;
using System.Numerics;
using Robust.Server.GameStates;
using Content.Shared._DeepLagoon.Admin;
using Content.Shared.Administration;
using Content.Shared.Administration.Logs;
using Content.Shared.Administration.Managers;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Admin;

public sealed partial class AdminMapUploadSystem : EntitySystem
{
    [Dependency] private ISharedAdminManager _admins = default!;
    [Dependency] private ISharedAdminLogManager _logs = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private PvsOverrideSystem _pvs = default!;

    private sealed record Preview(Guid Request, ICommonSession Session, EntityUid Ghost, EntityUid Destination,
        EntityUid Stage, LoadResult Loaded, Box2 Bounds, TimeSpan Expires);

    private readonly Dictionary<NetUserId, Preview> _previews = new();

    private sealed class Upload(Guid request, EntityUid ghost, int length, TimeSpan expires)
    {
        public readonly Guid Request = request;
        public readonly EntityUid Ghost = ghost;
        public readonly byte[] Bytes = new byte[length];
        public TimeSpan Expires = expires;
        public int Offset;
    }

    private readonly Dictionary<NetUserId, Upload> _uploads = new();
    private readonly Dictionary<NetUserId, TimeSpan> _cooldowns = new();

    public override void Initialize()
    {
        SubscribeNetworkEvent<AdminMapUploadBeginEvent>(OnBegin);
        SubscribeNetworkEvent<AdminMapUploadChunkEvent>(OnChunk);
        SubscribeNetworkEvent<AdminMapPlacementConfirmEvent>(OnConfirm);
        SubscribeNetworkEvent<AdminMapPlacementCancelEvent>(OnCancel);
    }

    public bool CanUpload(ICommonSession session) =>
        _admins.HasAdminFlag(session, AdminFlags.Mapping) &&
        session.AttachedEntity is { } uid &&
        !Deleted(uid) && !Terminating(uid) &&
        HasComp<GhostComponent>(uid) &&
        Transform(uid).MapID != MapId.Nullspace;

    private void Reply(ICommonSession session, Guid request, string message)
    {
        if (session.Channel.IsConnected)
            RaiseNetworkEvent(new AdminMapUploadResultEvent(request, message), session);
    }

    private void OnBegin(AdminMapUploadBeginEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!CanUpload(session))
        {
            Reply(session, ev.Request, "dl-map-upload-denied");
            return;
        }

        if (ev.Length is < 1 or > AdminMapUploadLimits.MaxBytes)
        {
            Reply(session, ev.Request, "dl-map-upload-size");
            return;
        }

        if (_uploads.ContainsKey(session.UserId) || _previews.ContainsKey(session.UserId) ||
            _uploads.Count + _previews.Count >= 8 ||
            _cooldowns.TryGetValue(session.UserId, out var until) && until > _timing.RealTime)
        {
            Reply(session, ev.Request, "dl-map-upload-busy");
            return;
        }

        _uploads.Add(session.UserId, new Upload(ev.Request, session.AttachedEntity!.Value, ev.Length,
            _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TimeoutSeconds)));
        _cooldowns[session.UserId] = _timing.RealTime + TimeSpan.FromSeconds(5);
        RaiseNetworkEvent(new AdminMapUploadProgressEvent(ev.Request, 0, ev.Length), session);
    }

    private void OnChunk(AdminMapUploadChunkEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_uploads.TryGetValue(session.UserId, out var upload) || upload.Request != ev.Request)
            return;

        // Recheck permission and possession on every chunk, including the final one.
        if (!CanUpload(session) || session.AttachedEntity != upload.Ghost)
        {
            _uploads.Remove(session.UserId);
            Reply(session, ev.Request, "dl-map-upload-denied");
            return;
        }

        if (upload.Expires <= _timing.RealTime || ev.Offset != upload.Offset ||
            ev.Bytes.Length is < 1 or > AdminMapUploadLimits.ChunkBytes ||
            ev.Bytes.Length > upload.Bytes.Length - upload.Offset)
        {
            _uploads.Remove(session.UserId);
            Reply(session, ev.Request, "dl-map-upload-interrupted");
            return;
        }

        ev.Bytes.CopyTo(upload.Bytes, upload.Offset);
        upload.Offset += ev.Bytes.Length;
        upload.Expires = _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TimeoutSeconds);
        RaiseNetworkEvent(new AdminMapUploadProgressEvent(ev.Request, upload.Offset, upload.Bytes.Length), session);
        if (upload.Offset != upload.Bytes.Length)
            return;

        // Consume before loading: repeated final chunks cannot spawn a second copy.
        _uploads.Remove(session.UserId);
        try
        {
            using var stream = new MemoryStream(upload.Bytes, false);
            using var reader = new StreamReader(stream);
            if (!TryPrepare(reader, out var stage, out var loaded, out var bounds))
            {
                Reply(session, ev.Request, "dl-map-upload-invalid");
                return;
            }
            var destination = _transform.GetMapCoordinates(upload.Ghost);
            var preview = new Preview(ev.Request, session, upload.Ghost, _maps.GetMap(destination.MapId),
                stage, loaded!, bounds, _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TimeoutSeconds));
            _previews.Add(session.UserId, preview);
            // Only the uploader receives this paused, isolated map. Nothing is added to the live map yet.
            _pvs.AddSessionOverride(stage, session);
            RaiseNetworkEvent(new AdminMapPlacementReadyEvent(ev.Request, GetNetEntity(stage),
                GetNetEntity(preview.Destination), bounds, destination.Position), session);
        }
        catch (Exception e)
        {
            Log.Warning($"Admin map upload from {session.UserId} failed: {e}");
            Reply(session, ev.Request, "dl-map-upload-invalid");
        }
    }

    private bool TryPrepare(TextReader reader, out EntityUid staging, out LoadResult? result, out Box2 bounds)
    {
        staging = _maps.CreateMap(out var stagingId, runMapInit: false);
        _pvs.RemoveForceSend(staging);
        LoadResult? loaded = null;
        var success = false;
        result = null;
        bounds = default;
        try
        {
            if (!_loader.TryLoadGeneric(reader, "admin map upload", out loaded,
                    new MapLoadOptions { MergeMap = stagingId }) || loaded.Grids.Count == 0 || loaded.Maps.Count != 0)
                return false;

            Box2? combined = null;
            foreach (var grid in loaded.Grids)
            {
                _pvs.RemoveForceSend(grid.Owner);
                var box = _transform.GetWorldMatrix(grid.Owner).TransformBox(grid.Comp.LocalAABB);
                combined = combined?.Union(box) ?? box;
            }
            bounds = combined!.Value;
            result = loaded;
            success = true;
            return true;
        }
        finally
        {
            if (!success && loaded != null)
            {
                foreach (var uid in loaded.Entities)
                {
                    if (!Deleted(uid))
                        Del(uid);
                }
            }
            if (!success && !Deleted(staging))
                Del(staging);
        }
    }

    private void Place(EntityUid stage, Box2 bounds, EntityUid destination, Vector2 position, Angle rotation)
    {
        // MapInit (random spawners, devices, etc.) is deliberately delayed until confirmation.
        _maps.InitializeMap(stage, unpause: false);
        var roots = new List<EntityUid>();
        var children = Transform(stage).ChildEnumerator;
        while (children.MoveNext(out var child))
            roots.Add(child);
        foreach (var uid in roots)
        {
            var xform = Transform(uid);
            var newPosition = AdminMapPlacementMath.Position(xform.LocalPosition, bounds.Center, position, rotation);
            var newRotation = xform.LocalRotation + rotation;
            _transform.SetCoordinates(uid, new EntityCoordinates(destination, newPosition));
            _transform.SetWorldRotation(uid, newRotation);
            if (HasComp<MapGridComponent>(uid))
                _pvs.AddForceSend(uid);
        }
    }

    private void OnConfirm(AdminMapPlacementConfirmEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_previews.TryGetValue(session.UserId, out var preview) || preview.Request != ev.Request)
            return;
        var valid = CanUpload(session) && session.AttachedEntity == preview.Ghost &&
            !Deleted(preview.Destination) && !Deleted(preview.Stage) &&
            Transform(preview.Ghost).MapUid == preview.Destination && preview.Expires > _timing.RealTime &&
            AdminMapPlacementMath.IsValid(ev.Position, ev.Rotation);
        _previews.Remove(session.UserId);
        var success = false;
        try
        {
            if (!valid)
                return;
            Place(preview.Stage, preview.Bounds, preview.Destination, ev.Position, new Angle(ev.Rotation));
            success = true;
            _logs.Add(LogType.AdminCommands, LogImpact.High,
                $"{session.UserId} placed an uploaded map at {ev.Position} on {ToPrettyString(preview.Destination)}, rotation {ev.Rotation}");
        }
        catch (Exception e)
        {
            Log.Warning($"Could not place admin map from {session.UserId}: {e}");
        }
        finally
        {
            Cleanup(preview, deleteLoaded: !success);
            Reply(session, ev.Request, success ? "dl-map-upload-success" : "dl-map-upload-denied");
        }
    }

    private void OnCancel(AdminMapPlacementCancelEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (_uploads.TryGetValue(session.UserId, out var upload) && upload.Request == ev.Request)
            _uploads.Remove(session.UserId);
        if (!_previews.TryGetValue(session.UserId, out var preview) || preview.Request != ev.Request)
            return;
        _previews.Remove(session.UserId);
        Cleanup(preview);
    }

    private void Cleanup(Preview preview, bool deleteLoaded = true)
    {
        _pvs.RemoveSessionOverride(preview.Stage, preview.Session);
        if (deleteLoaded)
        {
            foreach (var uid in preview.Loaded.Entities)
            {
                if (!Deleted(uid))
                    Del(uid);
            }
        }
        if (!Deleted(preview.Stage))
            Del(preview.Stage);
    }

    public override void Update(float frameTime)
    {
        foreach (var (user, preview) in _previews.ToArray())
        {
            if (preview.Expires > _timing.RealTime && preview.Session.Channel.IsConnected && CanUpload(preview.Session) &&
                preview.Session.AttachedEntity == preview.Ghost && Transform(preview.Ghost).MapUid == preview.Destination &&
                !Deleted(preview.Stage))
                continue;
            _previews.Remove(user);
            Cleanup(preview);
            Reply(preview.Session, preview.Request, "dl-map-upload-interrupted");
        }
        foreach (var (user, upload) in _uploads.ToArray())
        {
            if (upload.Expires <= _timing.RealTime || Deleted(upload.Ghost))
                _uploads.Remove(user);
        }
        foreach (var (user, until) in _cooldowns.ToArray())
        {
            if (until <= _timing.RealTime)
                _cooldowns.Remove(user);
        }
    }

    public override void Shutdown()
    {
        foreach (var preview in _previews.Values)
            Cleanup(preview);
        _previews.Clear();
        _uploads.Clear();
        _cooldowns.Clear();
        base.Shutdown();
    }
}
