using System.Linq;
using Content.Server.Fluids.EntitySystems;
using Content.Shared._DeepLagoon.Construction;
using Content.Shared.Chemistry.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Construction;

public sealed class ShowerSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private PuddleSystem _puddles = default!;

    private readonly HashSet<EntityUid> _active = new();
    private const int MaximumPuddleVolume = 10;
    private const int WaterPerSpill = 2;

    public override void Initialize()
    {
        SubscribeLocalEvent<ShowerComponent, InteractHandEvent>(OnHandInteract);
        SubscribeLocalEvent<ShowerComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<ShowerComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ShowerComponent, ComponentShutdown>((uid, _, _) => _active.Remove(uid));
        SubscribeLocalEvent<ShowerComponent, AnchorStateChangedEvent>(OnAnchorChanged);
    }

    private void OnStartup(EntityUid uid, ShowerComponent shower, ComponentStartup args)
    {
        shower.Mist = shower.Running && _timing.CurTime - shower.StartedAt > shower.MistDelay;
        if (shower.Running)
            _active.Add(uid);
        UpdateAppearance(uid, shower);
    }

    private void OnAnchorChanged(EntityUid uid, ShowerComponent shower, ref AnchorStateChangedEvent args)
    {
        if (!args.Anchored)
            SetRunning(uid, false, shower);
    }

    private void OnHandInteract(EntityUid uid, ShowerComponent shower, InteractHandEvent args)
    {
        if (args.Handled)
            return;
        SetRunning(uid, !shower.Running, shower);
        args.Handled = true;
    }

    private void OnActivate(EntityUid uid, ShowerComponent shower, ref ActivateInWorldEvent args)
    {
        if (args.Handled)
            return;
        SetRunning(uid, !shower.Running, shower);
        args.Handled = true;
    }

    public void SetRunning(EntityUid uid, bool running, ShowerComponent? shower = null)
    {
        if (TerminatingOrDeleted(uid) || !Resolve(uid, ref shower) || running && !Transform(uid).Anchored)
            return;
        if (shower.Running == running)
            return;
        shower.Running = running;
        shower.Mist = false;
        shower.StartedAt = _timing.CurTime;
        shower.NextSpill = _timing.CurTime;
        if (running)
            _active.Add(uid);
        else
            _active.Remove(uid);
        UpdateAppearance(uid, shower);
    }

    private void UpdateAppearance(EntityUid uid, ShowerComponent shower)
    {
        _appearance.SetData(uid, ShowerVisuals.Water, shower.Running);
        _appearance.SetData(uid, ShowerVisuals.Mist, shower.Running && shower.Mist);
    }

    public override void Update(float frameTime)
    {
        foreach (var uid in _active.ToArray())
        {
            if (TerminatingOrDeleted(uid) || !TryComp<ShowerComponent>(uid, out var shower) || !shower.Running)
            {
                _active.Remove(uid);
                continue;
            }
            if (MetaData(uid).EntityPaused)
                continue;
            var xform = Transform(uid);
            if (!xform.Anchored)
            {
                SetRunning(uid, false, shower);
                continue;
            }
            if (!shower.Mist && _timing.CurTime - shower.StartedAt > shower.MistDelay)
            {
                shower.Mist = true;
                _appearance.SetData(uid, ShowerVisuals.Mist, true);
            }
            if (_timing.CurTime < shower.NextSpill)
                continue;
            // No catch-up loop after a pause or a delayed frame.
            shower.NextSpill = _timing.CurTime + shower.SpillInterval;
            if (!TryComp<MapGridComponent>(xform.GridUid, out var grid))
                continue;
            var tile = _maps.GetTileRef(xform.GridUid.Value, grid, xform.Coordinates);
            var existing = _puddles.TryGetPuddle(tile, out var puddle) ? _puddles.CurrentVolume(puddle) : FixedPoint2.Zero;
            var amount = FixedPoint2.Min(WaterPerSpill, FixedPoint2.Max(FixedPoint2.Zero, MaximumPuddleVolume - existing));
            if (amount > 0)
                _puddles.TrySpillAt(tile, new Solution("Water", amount), out _, sound: false);
        }
    }
}
