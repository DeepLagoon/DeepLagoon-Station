using System.Numerics;
using System.Linq;
using Content.Shared._Mono.Radar;
using Content.Shared.GameTicking;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Robust.Shared.Network;
using Robust.Shared.Map.Components;

namespace Content.Client._Mono.Radar;

public sealed partial class RadarBlipsSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private IClientNetManager _net = default!;
    private sealed class Contacts(EntityUid map)
    {
        public readonly EntityUid Map = map;
        public TimeSpan Sample, NextRequest, Sent;
        public bool Received, Awaiting;
        public List<BlipNetData> Blips = new();
        public List<MissileVectorNetData> Missiles = new();
        public List<HitscanNetData> Hitscans = new();
        public List<BlipConfig> Palette = new();
        public readonly List<BlipData> DrawBlips = new();
        public readonly List<MissileVectorData> DrawMissiles = new();
    }
    private const double StaleSeconds = 3;
    private readonly Dictionary<EntityUid, Contacts> _reports = new();
    private readonly Dictionary<NetEntity, TimeSpan> _removed = new();
    private readonly List<BlipData> _emptyBlips = new();
    private readonly List<MissileVectorData> _emptyMissiles = new();
    private readonly List<HitscanNetData> _emptyHitscans = new();
    private EntityUid? _lastConsole;

    public override void Initialize()
    {
        SubscribeNetworkEvent<GiveBlipsEvent>((ev, _) => ApplyReport(ev));
        SubscribeNetworkEvent<BlipRemovalEvent>(RemoveBlip);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => ResetReports());
    }
    private void ResetReports()
    {
        _reports.Clear(); _removed.Clear(); _lastConsole = null;
    }
    private Contacts? Report(EntityUid? console, bool create = false)
    {
        console ??= _lastConsole;
        if (console is not { } uid || !Exists(uid) || Transform(uid).MapUid is not { } map) return null;
        if (_reports.TryGetValue(uid, out var contacts) && contacts.Map == map) return contacts;
        _reports.Remove(uid);
        if (!create) return null;
        if (_reports.Count >= 16) _reports.Remove(_reports.OrderBy(p => p.Value.NextRequest).First().Key);
        return _reports[uid] = new Contacts(map);
    }
    public bool ApplyReport(GiveBlipsEvent ev)
    {
        if (!TryGetEntity(ev.Radar, out var radar) || radar is not { } uid || Report(uid) is not { } state ||
            ev.Map != GetNetEntity(state.Map) || state.Received && ev.SampleTime <= state.Sample ||
            (_timing.CurTime - ev.SampleTime).TotalSeconds > StaleSeconds) return false;
        state.Sample = ev.SampleTime; state.Received = true; state.Awaiting = false;
        state.Blips = ev.Blips.Where(b => !_removed.TryGetValue(b.Uid, out var removed) || removed < ev.SampleTime).ToList();
        state.Missiles = ev.Missiles; state.Hitscans = ev.HitscanLines; state.Palette = ev.ConfigPalette;
        foreach (var key in _removed.Where(p => (_timing.CurTime - p.Value).TotalSeconds > StaleSeconds).Select(p => p.Key).ToArray()) _removed.Remove(key);
        return true;
    }
    private void RemoveBlip(BlipRemovalEvent ev)
    {
        _removed[ev.NetBlipUid] = ev.SampleTime;
        foreach (var state in _reports.Values)
        {
            state.Blips.RemoveAll(b => b.Uid == ev.NetBlipUid);
            state.Missiles.RemoveAll(b => b.Uid == ev.NetBlipUid);
        }
    }
    public void RequestBlips(EntityUid console)
    {
        if (Report(console, true) is not { } state) return;
        _lastConsole = console;
        var now = _timing.RealTime;
        if (now < state.NextRequest) return;
        if (state.Awaiting && now - state.Sent < TimeSpan.FromMilliseconds(500)) return;
        // Each open console owns its request cadence as well as its report. A second window
        // must not consume a global throttle and leave this window without contacts.
        state.NextRequest = now + TimeSpan.FromMilliseconds(100);
        state.Sent = now; state.Awaiting = true;
        _net.ClientSendMessage(new MsgRadarPoll { Radar = GetNetEntity(console) });
    }
    private bool Fresh(Contacts? state) => state is { Received: true } && (_timing.CurTime - state.Sample).TotalSeconds <= StaleSeconds;
    public List<BlipData> GetCurrentBlips(EntityUid? console = null)
    {
        var state = Report(console);
        if (!Fresh(state)) return _emptyBlips;
        state!.DrawBlips.Clear();
        var age = (float)Math.Clamp((_timing.CurTime - state.Sample).TotalSeconds, 0, StaleSeconds);
        foreach (var blip in state.Blips)
        {
            if (blip.ConfigIndex >= state.Palette.Count) continue;
            var coord = GetCoordinates(blip.Position);
            if (!coord.IsValid(EntityManager)) continue;
            var predicted = new EntityCoordinates(coord.EntityId, coord.Position + blip.Vel * age);
            var config = state.Palette[blip.ConfigIndex];
            var rotation = blip.Rotation;
            _map.TryFindGridAt(_xform.ToMapCoordinates(predicted), out var grid, out _);
            // Mounted equipment belongs to its hull. A map-space sample plus linear velocity
            // cannot follow rotation, and drifts away from the client's own hull transform.
            // Keep the map sample as a fallback when a remote grid is outside PVS.
            if (blip.Anchor is { } anchor)
            {
                var local = GetCoordinates(anchor.Position);
                if (local.IsValid(EntityManager) && HasComp<MapGridComponent>(local.EntityId) && Transform(local.EntityId).MapUid == state.Map)
                {
                    grid = local.EntityId; predicted = local;
                    rotation = _xform.GetWorldRotation(local.EntityId) + anchor.Rotation;
                }
            }
            if (grid != EntityUid.Invalid && blip.OnGridConfigIndex is { } index && index < state.Palette.Count) config = state.Palette[index];
            state.DrawBlips.Add(new(blip.Uid, predicted, rotation, grid != EntityUid.Invalid ? grid : null, config));
        }
        return state.DrawBlips;
    }
    public List<MissileVectorData> GetMissileLines(EntityUid? console = null)
    {
        var state = Report(console);
        if (!Fresh(state)) return _emptyMissiles;
        state!.DrawMissiles.Clear();
        var age = (float)Math.Clamp((_timing.CurTime - state.Sample).TotalSeconds, 0, StaleSeconds);
        foreach (var missile in state.Missiles)
        {
            var index = state.Blips.FindIndex(b => b.Uid == missile.Uid);
            if (index < 0) continue;
            var blip = state.Blips[index]; var coord = GetCoordinates(blip.Position);
            if (!coord.IsValid(EntityManager)) continue;
            var position = coord.Position + blip.Vel * age;
            var start = GetNetCoordinates(new EntityCoordinates(coord.EntityId, position));
            NetCoordinates End(double angle, float range) => GetNetCoordinates(new EntityCoordinates(coord.EntityId,
                position + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * range));
            var direction = blip.Rotation.Theta - Math.PI * .5;
            state.DrawMissiles.Add(new(missile.Uid, start, End(direction, missile.Range / 2), Color.FromHex("#00AACC")));
            if (missile.ScanArc > 0)
            {
                state.DrawMissiles.Add(new(missile.Uid, start, End(direction - missile.ScanArc.Theta * .5, missile.Range), Color.FromHex("#FF0040")));
                state.DrawMissiles.Add(new(missile.Uid, start, End(direction + missile.ScanArc.Theta * .5, missile.Range), Color.FromHex("#FF0040")));
            }
        }
        return state.DrawMissiles;
    }
    public List<HitscanNetData> GetHitscanLines(EntityUid? console = null)
    {
        var state = Report(console);
        return Fresh(state) ? state!.Hitscans : _emptyHitscans;
    }
}

public record struct BlipData
(
    NetEntity NetUid,
    EntityCoordinates Position,
    Angle Rotation,
    EntityUid? GridUid,
    BlipConfig Config
);

public record struct MissileVectorData
(
    NetEntity NetUid,
    NetCoordinates PositionStart,
    NetCoordinates PositionEnd,
    Color Color
);
