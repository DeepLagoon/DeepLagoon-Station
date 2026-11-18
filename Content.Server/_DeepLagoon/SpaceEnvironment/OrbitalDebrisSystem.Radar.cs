using System.Numerics;
using Content.Shared._Mono.Radar;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public sealed partial class OrbitalDebrisSystem
{
    private readonly record struct RadarObserver(EntityUid Map, Vector2 Position, float Range);
    private readonly record struct RadarContact(EntityUid Uid, NetEntity NetUid, NetEntity NetMap,
        Vector2 Position, Vector2 Velocity, float Extent);
    private readonly List<RadarObserver> _radarObservers = new();
    private readonly Dictionary<EntityUid, List<RadarContact>> _radarContactsByMap = new();
    private readonly HashSet<EntityUid> _reportedRadarContacts = new();
    private readonly List<EntityUid> _emptyRadarMaps = new();
    private static readonly TimeSpan RadarSnapshotInterval = TimeSpan.FromMilliseconds(250);
    private TimeSpan _radarSnapshotTime = TimeSpan.MinValue;
    private TimeSpan _nextRadarSnapshot = TimeSpan.MinValue;

    private void RefreshRadarContacts()
    {
        var now = _timing.CurTime;
        if (now >= _radarSnapshotTime && now < _nextRadarSnapshot) return;
        _radarSnapshotTime = now;
        _nextRadarSnapshot = now + RadarSnapshotInterval;
        foreach (var contacts in _radarContactsByMap.Values) contacts.Clear();
        foreach (var (uid, pile) in _piles)
        {
            if (TerminatingOrDeleted(uid) || !TryComp<TransformComponent>(uid, out var xform)
                || xform.MapUid != pile.Map) continue;
            if (!_radarContactsByMap.TryGetValue(pile.Map, out var contacts))
            {
                contacts = new List<RadarContact>();
                _radarContactsByMap.Add(pile.Map, contacts);
            }
            contacts.Add(new RadarContact(uid, GetNetEntity(uid), GetNetEntity(pile.Map),
                PilePosition(uid, pile), _physics.GetMapLinearVelocity(uid), pile.HalfExtent));
        }
        // Retain buffers on active maps without keeping historical, unloaded maps indefinitely.
        _emptyRadarMaps.Clear();
        foreach (var (map, contacts) in _radarContactsByMap)
            if (contacts.Count == 0) _emptyRadarMaps.Add(map);
        foreach (var map in _emptyRadarMaps) _radarContactsByMap.Remove(map);
    }

    /// <summary>
    /// Adds bounded, map-relative contacts to a console's private radar report. Neither the physical debris
    /// nor its children need to be replicated, and no contact is sent to other sessions or sector maps.
    /// </summary>
    public void AppendRadarContacts(List<EntityUid> sources, RadarConsoleComponent radar,
        List<BlipNetData> contacts, ushort config)
    {
        // Resolve each detector once per report, rather than once for every piece of debris.
        _radarObservers.Clear();
        var maxRange = MathF.Max(0f, radar.MaxRange);
        if (maxRange <= 0f) return;
        foreach (var source in sources)
        {
            if (TerminatingOrDeleted(source) || !TryComp<TransformComponent>(source, out var sourceXform)
                || sourceXform.MapUid is not { } map) continue;
            var sourcePosition = _transform.GetWorldPosition(sourceXform);
            var velocity = _physics.GetMapLinearVelocity(source);
            var hullExtent = 0f;
            if (sourceXform.GridUid is { } gridUid && TryComp<MapGridComponent>(gridUid, out var grid))
            {
                var (origin, rotation) = _transform.GetWorldPositionRotation(gridUid);
                var center = origin + rotation.RotateVec(grid.LocalAABB.Center);
                hullExtent = Vector2.Distance(sourcePosition, center) + grid.LocalAABB.Size.Length() * 0.5f;
            }
            var warningRange = MathF.Max(SharedRadarConsoleSystem.DefaultMaxRange,
                (velocity.Length() + MaximumDebrisSpeed) * RadarWarningSeconds + hullExtent + HullClearance);
            _radarObservers.Add(new RadarObserver(map, sourcePosition, warningRange));
        }
        if (_radarObservers.Count == 0) return;
        RefreshRadarContacts();
        var age = (float) (_timing.CurTime - _radarSnapshotTime).TotalSeconds;
        _reportedRadarContacts.Clear();
        foreach (var observer in _radarObservers)
        {
            if (!_radarContactsByMap.TryGetValue(observer.Map, out var candidates)) continue;
            foreach (var candidate in candidates)
            {
                // Reuse geometry across viewers, projecting the short-lived snapshot to this request's time.
                var position = candidate.Position + candidate.Velocity * age;
                var range = MathF.Min(maxRange, observer.Range + candidate.Extent);
                if (Vector2.DistanceSquared(position, observer.Position) > range * range) continue;
                if (_radarObservers.Count > 1 && !_reportedRadarContacts.Add(candidate.Uid)) continue;
                if (!_piles.TryGetValue(candidate.Uid, out var pile) || TerminatingOrDeleted(candidate.Uid)
                    || !TryComp<TransformComponent>(candidate.Uid, out var xform)
                    || xform.MapUid != observer.Map) continue;
                // Revalidate ownership/map before sending: released or removed debris must not leak from the cache.
                // Map-relative coordinates work even when the physical grid is outside the receiving client's PVS.
                contacts.Add(new BlipNetData(candidate.NetUid, new NetCoordinates(candidate.NetMap, position),
                    candidate.Velocity, Angle.Zero, config, null));
                pile.LastSeen = _timing.CurTime;
            }
        }
    }
}
