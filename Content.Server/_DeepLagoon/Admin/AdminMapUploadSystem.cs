using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Content.Shared.GameTicking;
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
using Robust.Shared.Map.Events;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
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
    [Dependency] private IPrototypeManager _prototypes = default!;

    private sealed record Preview(Guid Request, ICommonSession Session, EntityUid Ghost, EntityUid Destination,
        EntityUid Stage, LoadResult Loaded, Box2 Bounds, TimeSpan Expires);

    private readonly Dictionary<NetUserId, Preview> _previews = new();

    private sealed class Upload(Guid request, EntityUid ghost, int length, string name, string hash, TimeSpan expires)
    {
        public readonly Guid Request = request;
        public EntityUid Ghost = ghost;
        public readonly AdminMapUploadBuffer Buffer = new(length);
        public readonly string Name = name;
        public readonly string Hash = hash;
        public TimeSpan Expires = expires;
    }

    private sealed record StoredMap(AdminMapLibraryEntry Entry, byte[] Bytes, string Hash);
    private readonly Dictionary<Guid, StoredMap> _library = new();
    private readonly Dictionary<(NetUserId User, Guid Request), Guid> _completedUploads = new();
    private int _libraryBytes;
    private readonly Dictionary<NetUserId, Upload> _uploads = new();
    private readonly Dictionary<NetUserId, TimeSpan> _cooldowns = new();

    public override void Initialize()
    {
        SubscribeNetworkEvent<AdminMapUploadBeginEvent>(OnBegin);
        SubscribeNetworkEvent<AdminMapLibraryRequestEvent>(OnLibraryRequest);
        SubscribeNetworkEvent<AdminMapLibraryPlaceEvent>(OnLibraryPlace);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => ResetRound());
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

        if (ev.Length is < 1 or > AdminMapUploadLimits.MaxBytes || ev.Sha256 is not { Length: 64 } ||
            !ev.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(ev.Name) || ev.Name.Length > 128 ||
            ev.Name.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            Reply(session, ev.Request, "dl-map-upload-size");
            return;
        }

        // A repeated begin is a resume/status probe, never a second allocation.
        if (_uploads.TryGetValue(session.UserId, out var active) && active.Request == ev.Request)
        {
            if (active.Buffer.Bytes.Length != ev.Length || active.Hash != ev.Sha256 || active.Name != ev.Name)
            {
                Reply(session, ev.Request, "dl-map-upload-invalid");
                return;
            }
            active.Ghost = session.AttachedEntity!.Value;
            Acknowledge(session, active);
            return;
        }
        if (_previews.TryGetValue(session.UserId, out var preview) && preview.Request == ev.Request)
        {
            if (preview.Session == session && preview.Ghost == session.AttachedEntity && !Deleted(preview.Stage))
            {
                SendPreview(preview);
                return;
            }
            _previews.Remove(session.UserId);
            Cleanup(preview);
        }
        if (_completedUploads.TryGetValue((session.UserId, ev.Request), out var storedId) &&
            _library.TryGetValue(storedId, out var stored))
        {
            StartPreview(session, ev.Request, stored.Bytes);
            return;
        }
        if (_uploads.ContainsKey(session.UserId) || _previews.ContainsKey(session.UserId) ||
            _uploads.Count + _previews.Count >= 8 ||
            _cooldowns.TryGetValue(session.UserId, out var until) && until > _timing.RealTime)
        {
            Reply(session, ev.Request, "dl-map-upload-busy");
            return;
        }
        // Reserve storage for in-flight files as well; existing round files are never evicted.
        if (_library.Count + _uploads.Count >= AdminMapUploadLimits.LibraryMaxFiles ||
            _libraryBytes + _uploads.Values.Sum(u => u.Buffer.Bytes.Length) + ev.Length > AdminMapUploadLimits.LibraryMaxBytes)
        {
            Reply(session, ev.Request, "dl-map-upload-library-full");
            return;
        }
        var upload = new Upload(ev.Request, session.AttachedEntity!.Value, ev.Length, ev.Name, ev.Sha256,
            _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TransferTimeoutSeconds));
        _uploads.Add(session.UserId, upload);
        _cooldowns[session.UserId] = _timing.RealTime + TimeSpan.FromSeconds(5);
        Acknowledge(session, upload);
    }

    private void Acknowledge(ICommonSession session, Upload upload) =>
        RaiseNetworkEvent(new AdminMapUploadProgressEvent(upload.Request, upload.Buffer.Offset, upload.Buffer.Bytes.Length), session);

    private void OnChunk(AdminMapUploadChunkEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_uploads.TryGetValue(session.UserId, out var upload) || upload.Request != ev.Request)
            return;
        if (!CanUpload(session) || session.AttachedEntity != upload.Ghost)
        {
            _uploads.Remove(session.UserId);
            Reply(session, ev.Request, "dl-map-upload-denied");
            return;
        }
        if (upload.Expires <= _timing.RealTime || !upload.Buffer.Accept(ev.Offset, ev.Bytes, out var advanced))
        {
            _uploads.Remove(session.UserId);
            Reply(session, ev.Request, "dl-map-upload-interrupted");
            return;
        }
        // Only new data extends retention; duplicate probes cannot keep abandoned allocations alive forever.
        if (advanced)
            upload.Expires = _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TransferTimeoutSeconds);
        Acknowledge(session, upload);
        if (upload.Buffer.Offset != upload.Buffer.Bytes.Length)
            return;
        _uploads.Remove(session.UserId);
        var bytes = upload.Buffer.Bytes;
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(upload.Hash, StringComparison.OrdinalIgnoreCase))
        {
            Reply(session, ev.Request, "dl-map-upload-checksum");
            return;
        }
        if (!StartPreview(session, ev.Request, bytes))
            return;
        var entry = new AdminMapLibraryEntry(Guid.NewGuid(), upload.Name, bytes.Length);
        _library.Add(entry.Id, new StoredMap(entry, bytes, upload.Hash));
        _libraryBytes += bytes.Length;
        _completedUploads[(session.UserId, ev.Request)] = entry.Id;
    }

    private bool StartPreview(ICommonSession session, Guid request, byte[] bytes)
    {
        if (!CanUpload(session))
        {
            Reply(session, request, "dl-map-upload-denied");
            return false;
        }
        if (_previews.ContainsKey(session.UserId) || _uploads.ContainsKey(session.UserId) ||
            _previews.Count + _uploads.Count >= 8)
        {
            Reply(session, request, "dl-map-upload-busy");
            return false;
        }
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new StreamReader(stream);
            if (!TryPrepare(reader, out var stage, out var loaded, out var bounds, out var missing))
            {
                Reply(session, request, "dl-map-upload-invalid");
                return false;
            }
            var ghost = session.AttachedEntity!.Value;
            var destination = _transform.GetMapCoordinates(ghost);
            var preview = new Preview(request, session, ghost, _maps.GetMap(destination.MapId),
                stage, loaded!, bounds, _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TimeoutSeconds));
            _previews.Add(session.UserId, preview);
            _pvs.AddSessionOverride(stage, session);
            SendPreview(preview, missing);
            return true;
        }
        catch (Exception e)
        {
            Log.Warning($"Admin map upload from {session.UserId} failed: {e}");
            Reply(session, request, "dl-map-upload-invalid");
            return false;
        }
    }

    private void SendPreview(Preview preview, Dictionary<string, int>? missing = null)
    {
        // Reconstruct diagnostics when retrying a lost ready response.
        missing ??= preview.Loaded.Entities.Where(uid => HasComp<AdminMapMissingPrototypeComponent>(uid))
            .Select(uid => Comp<AdminMapMissingPrototypeComponent>(uid).Prototype)
            .GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
        RaiseNetworkEvent(new AdminMapPlacementReadyEvent(preview.Request, GetNetEntity(preview.Stage),
            GetNetEntity(preview.Destination), preview.Bounds, _transform.GetMapCoordinates(preview.Ghost).Position, missing), preview.Session);
    }

    private void OnLibraryRequest(AdminMapLibraryRequestEvent ev, EntitySessionEventArgs args)
    {
        if (_admins.IsAdmin(args.SenderSession))
            RaiseNetworkEvent(new AdminMapLibraryEvent(_library.Values.Select(item => item.Entry).ToArray()), args.SenderSession);
    }

    private void OnLibraryPlace(AdminMapLibraryPlaceEvent ev, EntitySessionEventArgs args)
    {
        if (!CanUpload(args.SenderSession))
        {
            Reply(args.SenderSession, ev.Request, "dl-map-upload-denied");
            return;
        }
        if (_previews.TryGetValue(args.SenderSession.UserId, out var preview) && preview.Request == ev.Request)
        {
            if (preview.Session == args.SenderSession && preview.Ghost == args.SenderSession.AttachedEntity && !Deleted(preview.Stage))
            {
                SendPreview(preview);
                return;
            }
            _previews.Remove(args.SenderSession.UserId);
            Cleanup(preview);
        }
        if (_library.TryGetValue(ev.Id, out var stored))
            StartPreview(args.SenderSession, ev.Request, stored.Bytes);
        else
            Reply(args.SenderSession, ev.Request, "dl-map-upload-library-missing");
    }

    private bool TryPrepare(TextReader reader, out EntityUid staging, out LoadResult? result, out Box2 bounds,
        out Dictionary<string, int> missing)
    {
        staging = _maps.CreateMap(out var stagingId, runMapInit: false);
        _pvs.RemoveForceSend(staging);
        LoadResult? loaded = null;
        var success = false;
        result = null;
        bounds = default;
        missing = new();
        try
        {
            var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
            if (documents.Length != 1 || documents[0].Root is not MappingDataNode data)
                return false;
            var migrations = new BeforeEntityReadEvent();
            RaiseLocalEvent(migrations);
            AdminMapUploadCompatibility.ReplaceMissing(data,
                id => _prototypes.HasIndex<EntityPrototype>(id), migrations.RenamedPrototypes);
            if (!_loader.TryLoadGeneric(data, "admin map upload", out loaded,
                    new MapLoadOptions { MergeMap = stagingId }) || loaded.Grids.Count == 0 || loaded.Maps.Count != 0)
                return false;

            foreach (var uid in loaded.Entities)
            {
                if (TryComp<AdminMapMissingPrototypeComponent>(uid, out var marker) && marker.Prototype.Length > 0)
                    missing[marker.Prototype] = missing.GetValueOrDefault(marker.Prototype) + 1;
            }
            if (missing.Count > 0)
                Log.Warning($"Admin map upload omitted {missing.Values.Sum()} entities with {missing.Count} missing prototypes.");

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
            if (upload.Expires <= _timing.RealTime)
                _uploads.Remove(user);
        }
        foreach (var (user, until) in _cooldowns.ToArray())
        {
            if (until <= _timing.RealTime)
                _cooldowns.Remove(user);
        }
    }

    private void ResetRound()
    {
        foreach (var preview in _previews.Values)
            Cleanup(preview);
        _previews.Clear();
        _uploads.Clear();
        _cooldowns.Clear();
        _library.Clear();
        _completedUploads.Clear();
        _libraryBytes = 0;
    }

    public override void Shutdown()
    {
        ResetRound();
        base.Shutdown();
    }
}
