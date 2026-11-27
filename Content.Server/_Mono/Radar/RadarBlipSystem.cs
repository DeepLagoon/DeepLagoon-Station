using System.Numerics;
using System.Linq;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server._Mono.Projectiles.TargetGuided;
using Content.Server._Mono.Projectiles.TargetSeeking;
using Content.Shared._Mono.Radar;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Network;

namespace Content.Server._Mono.Radar;

public sealed partial class RadarBlipSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private OrbitalDebrisSystem _debris = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IServerNetManager _net = default!;
    private readonly Dictionary<(ICommonSession Session, EntityUid Radar), TimeSpan> _nextRequest = new();
    private static readonly BlipConfig DebrisContact = new()
    {
        Bounds = new Box2(-3f, -3f, 3f, 3f),
        Color = Color.Orange,
        Shape = RadarBlipShape.Diamond,
    };

    // Pooled collections to avoid per-request heap churn
    private readonly List<BlipNetData> _tempBlipsCache = new();
    private readonly List<MissileVectorNetData> _tempMissileCache = new();
    private readonly List<HitscanNetData> _tempHitscansCache = new();
    private readonly List<EntityUid> _tempSourcesCache = new();
    private readonly List<BlipConfig> _tempPaletteCache = new();
    private readonly Dictionary<BlipConfig, ushort> _paletteIndex = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestBlipsEvent>(OnBlipsRequested);
        _net.RegisterNetMessage<MsgRadarPoll>(message =>
        {
            if (_players.TryGetSessionByChannel(message.MsgChannel, out var session))
                SendReport(message.Radar, session, telemetry: true);
        });
        _net.RegisterNetMessage<MsgRadarSnapshot>();
        SubscribeLocalEvent<RadarBlipComponent, ComponentShutdown>(OnBlipShutdown);
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        _nextRequest.Clear();
        base.Shutdown();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus == SessionStatus.InGame) return;
        foreach (var key in _nextRequest.Keys.Where(k => k.Session == args.Session).ToArray()) _nextRequest.Remove(key);
    }

    private void OnBlipsRequested(RequestBlipsEvent ev, EntitySessionEventArgs args)
        => SendReport(ev.Radar, args.SenderSession, telemetry: false);

    private void SendReport(NetEntity radarEntity, ICommonSession session, bool telemetry)
    {
        if (!TryGetEntity(radarEntity, out var radarUid) || radarUid is not { } console) return;
        var requestKey = (session, console);
        // Reject repeats for this console, without suppressing another authorized open radar.
        if (_nextRequest.TryGetValue(requestKey, out var next) && _timing.CurTime < next) return;
        if (!TryComp<RadarConsoleComponent>(radarUid, out var radar)
            || session.Status != SessionStatus.InGame
            || session.AttachedEntity is not { } actor
            || !TryComp<UserInterfaceUserComponent>(actor, out var uiUser)
            || !uiUser.OpenInterfaces.TryGetValue(radarUid.Value, out var openKeys)
        )
            return;

        // Contacts must not turn arbitrary console IDs into a remote space/player-position probe.
        var hasOpenUi = false;
        foreach (var key in openKeys)
        {
            if (!_ui.IsUiOpen(radarUid.Value, key, actor)) continue;
            hasOpenUi = true;
            break;
        }
        if (!hasOpenUi || Transform(radarUid.Value).MapUid is not { } reportMap) return;
        foreach (var key in _nextRequest.Keys.Where(k => k.Session == session && !uiUser.OpenInterfaces.ContainsKey(k.Radar)).ToArray())
            _nextRequest.Remove(key);
        _nextRequest[requestKey] = _timing.CurTime + TimeSpan.FromMilliseconds(100);

        var sourcesEv = new GetRadarSourcesEvent();
        RaiseLocalEvent(radarUid.Value, ref sourcesEv);

        // Reuse pooled sources list
        _tempSourcesCache.Clear();
        if (sourcesEv.Sources != null)
            _tempSourcesCache.AddRange(sourcesEv.Sources);
        else
            _tempSourcesCache.Add(radarUid.Value);

        AssembleBlipsReport((EntityUid)radarUid, _tempSourcesCache, radar);
        AssembleHitscanReport((EntityUid)radarUid, _tempSourcesCache, radar);

        // Combine the blips and hitscan lines
        var giveEv = new GiveBlipsEvent(_tempPaletteCache, _tempBlipsCache, _tempMissileCache, _tempHitscansCache,
            GetNetEntity(radarUid.Value), GetNetEntity(reportMap), _timing.CurTime);
        if (telemetry) _net.ServerSendMessage(new MsgRadarSnapshot { Snapshot = giveEv }, session.Channel);
        else RaiseNetworkEvent(giveEv, session);

        _tempBlipsCache.Clear();
        _tempMissileCache.Clear();
        _tempHitscansCache.Clear();
        _tempSourcesCache.Clear();
        _tempPaletteCache.Clear();
        _paletteIndex.Clear();
    }

    private void OnBlipShutdown(EntityUid blipUid, RadarBlipComponent component, ComponentShutdown args)
    {
        var netBlipUid = GetNetEntity(blipUid);
        var removalEv = new BlipRemovalEvent(netBlipUid, _timing.CurTime);
        RaiseNetworkEvent(removalEv);
    }

    private void AssembleBlipsReport(EntityUid uid, List<EntityUid> sources, RadarConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var radarXform = Transform(uid);
        var radarGrid = radarXform.GridUid;
        var radarMapId = radarXform.MapID;

        var blipQuery = EntityQueryEnumerator<RadarBlipComponent, TransformComponent, PhysicsComponent>();

        while (blipQuery.MoveNext(out var blipUid, out var blip, out var blipXform, out var blipPhysics))
        {
            if (!blip.Enabled
                || blipXform.MapID != radarMapId
                || !NearAnySources(_xform.GetWorldPosition(blipXform), sources, blip.MaxDistance)
            )
                continue;

            var blipGrid = blipXform.GridUid;

            if (blip.RequireNoGrid && blipGrid != null // if we want no grid but we are on a grid
                || !blip.VisibleFromOtherGrids && blipGrid != radarGrid // or if we don't want to be visible from other grids but we're on another grid
            )
                continue; // don't show this blip

            var netBlipUid = GetNetEntity(blipUid);

            var blipVelocity = _physics.GetMapLinearVelocity(blipUid, blipPhysics, blipXform);

            // due to PVS being a thing, things will break if we try to parent to not the map or a grid
            // Position, velocity and heading use the same map frame. A grid's movement must
            // not be applied twice to a projectile sample between reports.
            var coord = new EntityCoordinates(blipXform.MapUid!.Value, _xform.GetWorldPosition(blipXform));

            var gridCfg = (BlipConfig?)null;
            var rotation = _xform.GetWorldRotation(blipXform);
            GridBlipAnchor? anchor = null;

            // we're parented to either the map or a grid and this is relative velocity so account for grid movement
            if (blipGrid != null)
            {
                // and hijack our shape if we want to
                gridCfg = blip.GridConfig;
                if (blipXform.Anchored)
                {
                    var local = _xform.ToCoordinates(blipGrid.Value, _xform.ToMapCoordinates(coord));
                    anchor = new(GetNetCoordinates(local), rotation - _xform.GetWorldRotation(blipGrid.Value));
                }
            }

            var configIdx = GetOrAddConfig(blip.Config);
            ushort? gridConfigIdx = gridCfg is { } gridCf ? GetOrAddConfig(gridCf) : null;

            // ideally we would handle blips being culled by detection on server but detection grid culling is already clientside so might as well
            _tempBlipsCache.Add(new(netBlipUid,
                            GetNetCoordinates(coord),
                            blipVelocity,
                            rotation,
                            configIdx,
                            gridConfigIdx,
                            anchor));
        }

        if (_debris.ActiveCount > 0)
            _debris.AppendRadarContacts(sources, component, _tempBlipsCache, GetOrAddConfig(DebrisContact));

        var missileQuery = EntityQueryEnumerator<TargetSeekingComponent, RadarBlipComponent, TransformComponent>();
        while (missileQuery.MoveNext(out var missile, out var seeker, out var missileBlip, out var missileBlipXform))
        {
            var netMissileUid = GetNetEntity(missile);
            var missileArc = MathHelper.DegreesToRadians(seeker.ScanArc);
            if (seeker.ArcLines)
                _tempMissileCache.Add(new(netMissileUid,
                    (float)(seeker.MaxSpeed * 0.2),
                    missileArc));
        }
        // Check for SACLOS/mouse-guided missiles to add their directional lines to the console.
        var saclosQuery = EntityQueryEnumerator<TargetGuidedComponent, RadarBlipComponent, TransformComponent>();
        while (saclosQuery.MoveNext(out var missile, out var seeker, out var missileBlip, out var missileBlipXform))
        {
            var netMissileUid = GetNetEntity(missile);
            if (seeker.RadarLines)
                _tempMissileCache.Add(new(netMissileUid,
                    (float)(seeker.CurrentSpeed * 0.2),
                    0));
        }
    }

    /// <summary>
    /// Gets or create palette index for blip config.
    /// </summary>
    private ushort GetOrAddConfig(BlipConfig config)
    {
        if (_paletteIndex.TryGetValue(config, out var index))
            return index;

        if (_tempPaletteCache.Count >= ushort.MaxValue)
        {
            Log.Error($"Blip config count overflow! Reached max {ushort.MaxValue}, but trying to add more.");
            return 0;
        }

        index = (ushort)_tempPaletteCache.Count;
        _tempPaletteCache.Add(config);
        _paletteIndex[config] = index;
        return index;
    }

    /// <summary>
    /// Assembles trajectory information for hitscan projectiles to be displayed on radar
    /// </summary>
    private void AssembleHitscanReport(EntityUid uid, List<EntityUid> sources, RadarConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var radarXform = Transform(uid);

        var hitscanQuery = EntityQueryEnumerator<HitscanRadarComponent>();

        while (hitscanQuery.MoveNext(out var hitscanUid, out var hitscan))
        {
            if (!hitscan.Enabled)
                continue;

            if (!NearAnySources(hitscan.StartPosition, sources, component.MaxRange) && NearAnySources(hitscan.EndPosition, sources, component.MaxRange))
                continue;

            _tempHitscansCache.Add(new(hitscan.StartPosition, hitscan.EndPosition, hitscan.LineThickness, hitscan.RadarColor));
        }
    }

    private bool NearAnySources(Vector2 coord, List<EntityUid> sources, float range)
    {
        var rsqr = range * range;
        foreach (var source in sources)
        {
            var pos = _xform.GetWorldPosition(source);
            if ((pos - coord).LengthSquared() < rsqr)
                return true;
        }
        return false;
    }
}
