using Content.Server.Atmos.EntitySystems;
using Content.Server.Gravity;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Gravity;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEnvironmentSystem : EntitySystem
{
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private GravitySystem _gravity = default!;
    private float _elapsed;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(AtmosphereSystem));
        SubscribeLocalEvent<GridEnvironmentComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<GridEnvironmentComponent, EntParentChangedMessage>(OnParentChanged, after: new[] { typeof(AtmosphereSystem) });
        SubscribeLocalEvent<TileChangedEvent>(OnTilesChanged, after: new[] { typeof(AtmosphereSystem) });
    }

    private void OnMapInit(Entity<GridEnvironmentComponent> grid, ref MapInitEvent args) => RefreshGrid(grid, grid.Comp);
    private void OnParentChanged(Entity<GridEnvironmentComponent> grid, ref EntParentChangedMessage args) => RefreshGrid(grid, grid.Comp);

    private void OnTilesChanged(ref TileChangedEvent args)
    {
        if (TryComp<GridEnvironmentComponent>(args.Entity.Owner, out var environment) && environment.SafeAtmosphere)
            _atmos.SetSafeGridAtmosphere(args.Entity.Owner);
    }

    public void RefreshGrid(EntityUid uid, GridEnvironmentComponent environment)
    {
        if (TerminatingOrDeleted(uid) || !HasComp<MapGridComponent>(uid)) return;
        if (environment.SafeAtmosphere) _atmos.SetSafeGridAtmosphere(uid);
        var gravity = EnsureComp<GravityComponent>(uid);
        var previous = gravity.Enabled;
        if (environment.ForceGravity)
        {
            if (!environment.GravityCaptured)
            {
                environment.OriginalGravityEnabled = gravity.Enabled;
                environment.OriginalGravityInherent = gravity.Inherent;
                environment.GravityCaptured = true;
            }
            gravity.Enabled = gravity.Inherent = true;
        }
        else if (environment.GravityCaptured)
        {
            gravity.Enabled = environment.OriginalGravityEnabled;
            gravity.Inherent = environment.OriginalGravityInherent;
            environment.GravityCaptured = false;
            _gravity.RefreshGravity(uid, gravity);
        }
        if (previous != gravity.Enabled)
        {
            var changed = new GravityChangedEvent(uid, gravity.Enabled);
            RaiseLocalEvent(uid, ref changed, true);
        }
        Dirty(uid, gravity);
        RefreshPower();
    }

    public void RefreshPower()
    {
        var receivers = AllEntityQuery<ApcPowerReceiverComponent, TransformComponent>();
        while (receivers.MoveNext(out var uid, out var receiver, out var transform))
        {
            if (TerminatingOrDeleted(uid) || transform.GridUid is { } grid && TerminatingOrDeleted(grid)) continue;
            var bypass = TryComp<GridEnvironmentComponent>(transform.GridUid, out var environment) && environment.AlwaysPowered;
            if (bypass)
            {
                if (!HasComp<GridPowerOverrideComponent>(uid))
                    AddComp(uid, new GridPowerOverrideComponent { OriginalNeedsPower = receiver.NeedsPower, OriginalPowerDisabled = receiver.PowerDisabled });
                _power.SetNeedsPower(uid, false, receiver);
                _power.SetPowerDisabled(uid, false, receiver);
            }
            else if (TryComp<GridPowerOverrideComponent>(uid, out var original))
            {
                _power.SetNeedsPower(uid, original.OriginalNeedsPower, receiver);
                _power.SetPowerDisabled(uid, original.OriginalPowerDisabled, receiver);
                RemComp<GridPowerOverrideComponent>(uid);
            }
        }
        var consumers = AllEntityQuery<PowerConsumerComponent, TransformComponent>();
        while (consumers.MoveNext(out var uid, out var consumer, out var transform))
        {
            if (TerminatingOrDeleted(uid) || transform.GridUid is { } grid && TerminatingOrDeleted(grid)) continue;
            var bypass = TryComp<GridEnvironmentComponent>(transform.GridUid, out var environment) && environment.AlwaysPowered;
            if (bypass)
            {
                if (!HasComp<GridConsumerOverrideComponent>(uid))
                    AddComp(uid, new GridConsumerOverrideComponent { OriginalBypass = consumer.PowerBypass, OriginalNetworkEnabled = consumer.NetworkLoad.Enabled });
                consumer.PowerBypass = true;
                consumer.NetworkLoad.Enabled = false;
            }
            else if (TryComp<GridConsumerOverrideComponent>(uid, out var original))
            {
                consumer.PowerBypass = original.OriginalBypass;
                consumer.NetworkLoad.Enabled = original.OriginalNetworkEnabled;
                RemComp<GridConsumerOverrideComponent>(uid);
            }
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _elapsed += frameTime;
        if (_elapsed < 1) return;
        _elapsed = 0;
        var grids = AllEntityQuery<GridEnvironmentComponent, MapGridComponent>();
        while (grids.MoveNext(out var uid, out var environment, out _))
        {
            if (TerminatingOrDeleted(uid)) continue;
            if (environment.ForceGravity && (!TryComp<GravityComponent>(uid, out var gravity) || !gravity.Enabled || !gravity.Inherent)) RefreshGrid(uid, environment);
            if (environment.SafeAtmosphere) _atmos.SetSafeGridAtmosphere(uid);
        }
        RefreshPower();
    }
}
