using System.Numerics;
using Content.Server.Radio;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Damage;
using Content.Shared._Crescent.ShipShields;
using Content.Server.Power.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public sealed class SpaceEnvironmentSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SectorSystem _sectors = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    private readonly Dictionary<EntityUid, SpaceRegionIndex> _indexes = new();
    private readonly Dictionary<EntityUid, ShipState> _ships = new();
    private readonly HashSet<SpaceRegion> _candidates = new();
    private readonly HashSet<EntityUid> _seen = new();
    private readonly List<EntityUid> _remove = new();
    private TimeSpan _nextUpdate;
    public const float SafeDustSpeed = 12f;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ImpactInterval = TimeSpan.FromSeconds(5);

    private sealed class ShipState
    {
        public EntityUid Map;
        public Vector2 Position;
        public TimeSpan Entered;
        public TimeSpan NextImpact;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ConsoleFTLAttemptEvent>(OnFtlAttempt);
        SubscribeLocalEvent<ShipShieldedComponent, SpaceEnvironmentHitEvent>(OnShieldHit);
        SubscribeLocalEvent<RadioSendAttemptEvent>(OnRadioSend);
        SubscribeLocalEvent<RadioReceiveAttemptEvent>(OnRadioReceive);
        SubscribeLocalEvent<RadiationReceiverComponent, GetAmbientRadiationEvent>(OnAmbientRadiation);
    }

    public void Invalidate(EntityUid map) => _indexes.Remove(map);

    public void RemoveSector(EntityUid uid)
    {
        _indexes.Remove(uid);
        _remove.Clear();
        foreach (var (ship, state) in _ships)
            if (state.Map == uid) _remove.Add(ship);
        foreach (var ship in _remove) Clear(ship);
    }

    private SpaceRegionIndex Index(EntityUid map, SpaceEnvironmentComponent environment)
    {
        if (!_indexes.TryGetValue(map, out var index))
            _indexes.Add(map, index = new SpaceRegionIndex(environment.Definition.Regions));
        return index;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(1);
        _seen.Clear();
        if (_sectors.Enabled)
        {
            var ships = EntityQueryEnumerator<ShuttleComponent, TransformComponent>();
            while (ships.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.MapUid is not { } map || !TryComp<SpaceEnvironmentComponent>(map, out var environment)
                    || HasComp<FTLComponent>(uid)) continue;
                _seen.Add(uid);
                UpdateShip(uid, xform, map, environment);
            }
        }
        _remove.Clear();
        foreach (var ship in _ships.Keys)
            if (!_seen.Contains(ship)) _remove.Add(ship);
        foreach (var ship in _remove) Clear(ship);
    }

    private void UpdateShip(EntityUid uid, TransformComponent xform, EntityUid map, SpaceEnvironmentComponent environment)
    {
        var position = _transform.GetWorldPosition(xform);
        var first = !_ships.TryGetValue(uid, out var state) || state.Map != map;
        if (first) _ships[uid] = state = new ShipState { Map = map, Position = position, Entered = _timing.CurTime };
        Index(map, environment).Query(state!.Position, position, _candidates);
        var dust = 0f;
        var plasma = 0f;
        var radiation = 0f;
        foreach (var region in _candidates)
        {
            if (!SpaceRegionGeometry.Intersects(region, state.Position, position)) continue;
            switch (region.Kind)
            {
                case SpaceRegionKind.Plasma: plasma = MathF.Max(plasma, region.Intensity); break;
                case SpaceRegionKind.Radiation: radiation = MathF.Max(radiation, region.Intensity); break;
                default: dust = MathF.Max(dust, region.Intensity); break;
            }
        }
        state.Position = position;
        if (TryComp<SpaceEnvironmentProtectionComponent>(uid, out var protection))
        {
            dust *= Math.Clamp(protection.DustMultiplier, 0, 1);
            plasma *= Math.Clamp(protection.PlasmaMultiplier, 0, 1);
            radiation *= Math.Clamp(protection.RadiationMultiplier, 0, 1);
        }
        if (dust == 0 && plasma == 0 && radiation == 0)
        {
            state.Entered = _timing.CurTime;
            if (HasComp<SpaceExposureComponent>(uid)) RemComp<SpaceExposureComponent>(uid);
            return;
        }
        var exposure = EnsureComp<SpaceExposureComponent>(uid);
        if (exposure.Dust != dust || exposure.Plasma != plasma || exposure.Radiation != radiation)
        {
            exposure.Dust = dust;
            exposure.Plasma = plasma;
            exposure.Radiation = radiation;
            Dirty(uid, exposure);
        }
        if (dust <= 0 || _timing.CurTime - state.Entered < Grace || _timing.CurTime < state.NextImpact
            || !TryComp<PhysicsComponent>(uid, out var body) || body.LinearVelocity.Length() <= SafeDustSpeed) return;
        state.NextImpact = _timing.CurTime + ImpactInterval;
        var hit = new SpaceEnvironmentHitEvent(dust * 40f);
        RaiseLocalEvent(uid, ref hit);
        if (!hit.Absorbed) DamageHull(uid, dust, body.LinearVelocity.Length());
    }

    private void DamageHull(EntityUid grid, float intensity, float speed)
    {
        if (!TryComp<MapGridComponent>(grid, out var geometry)) return;
        var bounds = geometry.LocalAABB;
        var children = Transform(grid).ChildEnumerator;
        var inspected = 0;
        while (children.MoveNext(out var child) && inspected++ < 256)
        {
            var transform = Transform(child);
            if (!transform.Anchored || !HasComp<DamageableComponent>(child)) continue;
            var local = transform.LocalPosition;
            if (local.X > bounds.Left + 1.5f && local.X < bounds.Right - 1.5f
                && local.Y > bounds.Bottom + 1.5f && local.Y < bounds.Top - 1.5f) continue;
            var damage = new DamageSpecifier();
            damage.DamageDict["Blunt"] = MathF.Min(speed / SafeDustSpeed, 4f) * intensity * 2f;
            _damage.TryChangeDamage(child, damage);
            break;
        }
    }

    private void OnShieldHit(Entity<ShipShieldedComponent> ent, ref SpaceEnvironmentHitEvent args)
    {
        if (ent.Comp.Source is not { } source || TerminatingOrDeleted(ent.Comp.Shield)
            || !TryComp<ShipShieldEmitterComponent>(source, out var emitter) || emitter.Recharging
            || emitter.OverloadAccumulator > 0 || !TryComp<ApcPowerReceiverComponent>(source, out var power) || !power.Powered) return;
        emitter.Damage += args.Load;
        args.Absorbed = true;
    }

    private void Clear(EntityUid uid)
    {
        _ships.Remove(uid);
        if (!TerminatingOrDeleted(uid) && HasComp<SpaceExposureComponent>(uid)) RemComp<SpaceExposureComponent>(uid);
    }

    public bool BlocksFtl(EntityUid map, Vector2 position)
    {
        if (!_sectors.Enabled || !TryComp<SpaceEnvironmentComponent>(map, out var environment)) return false;
        Index(map, environment).Query(position, position, _candidates);
        foreach (var region in _candidates)
            if (region.Kind == SpaceRegionKind.Plasma && SpaceRegionGeometry.Contains(region, position)) return true;
        return false;
    }

    private void OnFtlAttempt(ref ConsoleFTLAttemptEvent args)
    {
        var xform = Transform(args.Uid);
        if (xform.MapUid is not { } map || !BlocksFtl(map, _transform.GetWorldPosition(xform))) return;
        args.Cancelled = true;
        args.Reason = Loc.GetString("space-environment-ftl-blocked");
    }

    private bool RadioBlocked(EntityUid uid) => _sectors.Enabled && Transform(uid).GridUid is { } grid
        && TryComp<SpaceExposureComponent>(grid, out var exposure) && exposure.Plasma > 0.6f;
    private void OnRadioSend(ref RadioSendAttemptEvent args) => args.Cancelled |= RadioBlocked(args.RadioSource);
    private void OnRadioReceive(ref RadioReceiveAttemptEvent args) => args.Cancelled |= RadioBlocked(args.RadioReceiver) || RadioBlocked(args.RadioSource);

    private void OnAmbientRadiation(Entity<RadiationReceiverComponent> ent, ref GetAmbientRadiationEvent args)
    {
        if (!_sectors.Enabled || Transform(ent).MapUid is not { } map
            || !TryComp<SpaceEnvironmentComponent>(map, out var environment)) return;
        var position = _transform.GetWorldPosition(ent);
        Index(map, environment).Query(position, position, _candidates);
        var strongest = 0f;
        foreach (var region in _candidates)
            if (region.Kind == SpaceRegionKind.Radiation && SpaceRegionGeometry.Contains(region, position))
                strongest = MathF.Max(strongest, region.Intensity);
        if (Transform(ent).GridUid is { } grid && TryComp<SpaceEnvironmentProtectionComponent>(grid, out var protection))
            strongest *= Math.Clamp(protection.RadiationMultiplier, 0, 1);
        args.Radiation += strongest * 0.5f;
    }
}
