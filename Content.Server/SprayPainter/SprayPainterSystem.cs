using Content.Server.Atmos.Piping.Components;
using Content.Server.Atmos.Piping.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.SprayPainter;
using Content.Shared.SprayPainter.Components;
using System.Numerics;
using Content.Server.Decals;
using Content.Shared.ActionBlocker;
using Content.Shared.Decals;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.SprayPainter;

/// <summary>
/// Handles spraying pipes using a spray painter.
/// Airlocks are handled in shared.
/// </summary>
public sealed partial class SprayPainterSystem : SharedSprayPainterSystem
{
    [Dependency] private AtmosPipeColorSystem _pipeColor = default!;
    [Dependency] private DecalSystem _decals = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMapSystem _maps = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SprayPainterComponent, SprayPainterPipeDoAfterEvent>(OnPipeDoAfter);

        SubscribeLocalEvent<AtmosPipeColorComponent, InteractUsingEvent>(OnPipeInteract);
        SubscribeLocalEvent<SprayPainterComponent, AfterInteractEvent>(OnDecalInteract);
        Subs.BuiEvents<SprayPainterComponent>(SprayPainterUiKey.Key, subs =>
        {
            subs.Event<SprayPainterPlaceDecalMessage>(OnPlaceDecal);
            subs.Event<SprayPainterEraseDecalMessage>(OnEraseDecal);
        });
    }

    private void OnPipeDoAfter(Entity<SprayPainterComponent> ent, ref SprayPainterPipeDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        if (args.Args.Target is not {} target)
            return;

        if (!TryComp<AtmosPipeColorComponent>(target, out var color))
            return;

        Audio.PlayPvs(ent.Comp.SpraySound, ent);

        _pipeColor.SetColor(target, color, args.Color);

        args.Handled = true;
    }

    private void OnPipeInteract(Entity<AtmosPipeColorComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!TryComp<SprayPainterComponent>(args.Used, out var painter) || painter.Mode != SprayPainterMode.Pipes)
            return;

        var color = painter.PipeColor;

        var doAfterEventArgs = new DoAfterArgs(EntityManager, args.User, painter.PipeSprayTime, new SprayPainterPipeDoAfterEvent(color), args.Used, target: ent, used: args.Used)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            // multiple pipes can be sprayed at once just not the same one
            DuplicateCondition = DuplicateConditions.SameTarget,
            NeedHand = true,
        };

        args.Handled = DoAfter.TryStartDoAfter(doAfterEventArgs);
    }

    private bool CanPaint(EntityUid item, EntityUid actor, EntityCoordinates location)
        => location.IsValid(EntityManager) && float.IsFinite(location.X) && float.IsFinite(location.Y)
            && _hands.IsHolding(actor, item) && _blocker.CanInteract(actor, item)
            && _interaction.InRangeUnobstructed(actor, location);

    private void OnDecalInteract(EntityUid uid, SprayPainterComponent painter, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || painter.Mode != SprayPainterMode.Decals)
            return;
        var location = args.ClickLocation;
        if (!CanPaint(uid, args.User, location))
            return;
        var gridUid = _transform.GetGrid(location);
        // Ground clicks may be map-relative. GetGrid only checks the coordinate's
        // parent, so resolve the grid under the point before applying local snap/offset.
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
        {
            if (!_maps.TryFindGridAt(_transform.ToMapCoordinates(location), out var foundGrid, out grid))
                return;
            gridUid = foundGrid;
        }
        location = _transform.WithEntityId(location, gridUid.Value);
        if (painter.DecalSnap)
            location = location.WithPosition(location.Position.Floored() + grid.TileSizeHalfVector);
        location = location.Offset(new Vector2(-0.5f, -0.5f));
        var decal = new Decal(location.Position, painter.DecalId, painter.DecalColor,
            Angle.FromDegrees(painter.DecalRotation), painter.DecalZIndex, true);
        if (ValidDecal(decal) && _decals.TryAddDecal(decal, location, out _))
        {
            Audio.PlayPvs(painter.SpraySound, uid);
            args.Handled = true;
        }
    }

    private void OnPlaceDecal(Entity<SprayPainterComponent> ent, ref SprayPainterPlaceDecalMessage args)
    {
        var location = GetCoordinates(args.Coordinates);
        // Placement coordinates use the decal's bottom-left, whereas interaction range is checked at its center.
        if (ent.Comp.Mode != SprayPainterMode.Decals || !ValidDecal(args.Decal)
            || !CanPaint(ent, args.Actor, location.Offset(new Vector2(0.5f, 0.5f))))
            return;
        if (_transform.GetGrid(location) is not { } grid || location.EntityId != grid)
            return;
        var decal = new Decal(location.Position, args.Decal.Id, args.Decal.Color, args.Decal.Angle, args.Decal.ZIndex, true);
        if (_decals.TryAddDecal(decal, location, out _))
            Audio.PlayPvs(ent.Comp.SpraySound, ent);
    }

    private void OnEraseDecal(Entity<SprayPainterComponent> ent, ref SprayPainterEraseDecalMessage args)
    {
        var location = GetCoordinates(args.Coordinates);
        if (ent.Comp.Mode != SprayPainterMode.Decals || !CanPaint(ent, args.Actor, location)
            || _transform.GetGrid(location) is not { } grid)
            return;
        var local = _transform.WithEntityId(location, grid);
        foreach (var (id, _) in _decals.GetDecalsInRange(grid, local.Position, validDelegate: decal => decal.Cleanable))
            _decals.RemoveDecal(grid, id);
    }
}
