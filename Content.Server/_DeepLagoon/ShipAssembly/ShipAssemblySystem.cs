using Content.Server._Crescent.Hardpoint;
using Content.Server.Administration.Logs;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.ShipAssembly;

public sealed class ShipAssemblySystem : EntitySystem
{
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private HardpointSystem _hardpoints = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TurfSystem _turf = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ShipAssemblyKitComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<ShipAssemblyKitComponent, ExaminedEvent>(OnExamined);
    }

    private void OnAfterInteract(Entity<ShipAssemblyKitComponent> kit, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        args.Handled = true;
        Deploy(kit, args.User, args.Target, args.ClickLocation);
    }

    private void OnExamined(Entity<ShipAssemblyKitComponent> kit, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange || !_prototypes.TryIndex(kit.Comp.Prototype, out var result))
            return;

        var message = result.HasComp<HardpointComponent>(EntityManager.ComponentFactory)
            ? "ship-assembly-kit-examine-hardpoint"
            : "ship-assembly-kit-examine-weapon";
        args.PushMarkup(Loc.GetString(message));
    }

    private void Deploy(Entity<ShipAssemblyKitComponent> kit, EntityUid user, EntityUid? target, EntityCoordinates coordinates)
    {
        if (TerminatingOrDeleted(kit) || EntityManager.IsQueuedForDeletion(kit) ||
            !_prototypes.TryIndex(kit.Comp.Prototype, out var result) ||
            !result.TryComp<FixturesComponent>(out var fixtures, EntityManager.ComponentFactory))
        {
            return;
        }

        EntityUid? mount = null;
        HardpointComponent? hardpoint = null;
        if (result.TryComp<HardpointAnchorableOnlyComponent>(out var weapon, EntityManager.ComponentFactory))
        {
            if (target is not { } targetUid || TerminatingOrDeleted(targetUid) || EntityManager.IsQueuedForDeletion(targetUid) ||
                !TryComp(targetUid, out hardpoint) ||
                !Transform(targetUid).Anchored ||
                (hardpoint.CompatibleTypes & weapon.CompatibleTypes) == 0 ||
                hardpoint.CompatibleSizes < weapon.CompatibleSizes)
            {
                _popup.PopupEntity(Loc.GetString("ship-assembly-kit-incompatible"), user, user);
                return;
            }

            if (hardpoint.anchoring is { } occupant)
            {
                if (EntityManager.EntityExists(occupant))
                {
                    _popup.PopupEntity(Loc.GetString("ship-assembly-kit-mount-occupied"), user, user);
                    return;
                }

                // The previous gun may have been destroyed without clearing the mount's reference.
                hardpoint.anchoring = null;
                Dirty(targetUid, hardpoint);
            }

            mount = targetUid;
            coordinates = Transform(targetUid).Coordinates;
        }
        else if (!result.HasComp<HardpointComponent>(EntityManager.ComponentFactory))
        {
            return;
        }

        if (!TryGetHullTile(coordinates, out var tile, out var grid))
        {
            _popup.PopupEntity(Loc.GetString("ship-assembly-kit-invalid-tile"), user, user);
            return;
        }

        var (layer, mask) = SharedPhysicsSystem.GetHardCollision(fixtures);
        if (!TileFree(grid, tile.GridIndices, layer, mask, mount))
        {
            _popup.PopupEntity(Loc.GetString("ship-assembly-kit-tile-occupied"), user, user);
            return;
        }

        var placement = _map.GridTileToLocal(grid.Owner, grid.Comp, tile.GridIndices);
        var rotation = mount is { } installedOn
            ? Transform(installedOn).LocalRotation
            : (_transform.GetWorldRotation(user) - _transform.GetWorldRotation(grid.Owner)).GetCardinalDir().ToAngle();
        var assembled = SpawnAttachedTo(result.ID, placement, rotation: rotation);
        var transform = Transform(assembled);
        if (!transform.Anchored && !_transform.AnchorEntity(assembled, tile))
        {
            Del(assembled);
            _popup.PopupEntity(Loc.GetString("ship-assembly-kit-invalid-tile"), user, user);
            return;
        }

        // Map initialization normally binds the gun to its hardpoint. It may be deferred on an uninitialized map.
        if (mount is { } anchor && hardpoint != null)
        {
            var assembledWeapon = Comp<HardpointAnchorableOnlyComponent>(assembled);
            if (assembledWeapon.anchoredTo == null && hardpoint.anchoring == null)
                _hardpoints.AnchorEntityToHardpoint(assembled, anchor, assembledWeapon, hardpoint, grid.Owner);

            if (assembledWeapon.anchoredTo != anchor || hardpoint.anchoring != assembled)
            {
                _transform.Unanchor(assembled);
                Del(assembled);
                _popup.PopupEntity(Loc.GetString("ship-assembly-kit-tile-occupied"), user, user);
                return;
            }
        }

        _adminLog.Add(LogType.Construction, LogImpact.Low,
            $"{ToPrettyString(user):player} installed {ToPrettyString(assembled):entity} from {ToPrettyString(kit):entity} at {placement:targetlocation}");
        QueueDel(kit);
        _popup.PopupEntity(Loc.GetString("ship-assembly-kit-installed", ("entity", assembled)), assembled, user);
    }

    private bool TryGetHullTile(EntityCoordinates coordinates, out TileRef tile, out Entity<MapGridComponent> grid)
    {
        tile = default;
        grid = default;
        if (!coordinates.IsValid(EntityManager))
            return false;

        var gridUid = _transform.GetGrid(coordinates);
        if (!TryComp<MapGridComponent>(gridUid, out var gridComp))
        {
            if (!_map.TryFindGridAt(_transform.ToMapCoordinates(coordinates), out var foundGrid, out gridComp))
                return false;

            gridUid = foundGrid;
        }

        if (gridUid is not { } hull || TerminatingOrDeleted(hull) || EntityManager.IsQueuedForDeletion(hull) ||
            !_map.TryGetTileRef(hull, gridComp, coordinates, out tile) ||
            tile.Tile.IsEmpty || _turf.IsSpace(tile) || !_turf.GetContentTileDefinition(tile).Sturdy)
        {
            return false;
        }

        grid = (hull, gridComp);
        return true;
    }

    private bool TileFree(Entity<MapGridComponent> grid, Vector2i indices, int layer, int mask, EntityUid? mount)
    {
        foreach (var entity in _map.GetAnchoredEntities(grid, indices))
        {
            if (entity == mount)
                continue;

            if (HasComp<HardpointComponent>(entity) || HasComp<HardpointAnchorableOnlyComponent>(entity))
                return false;

            if (TryComp<PhysicsComponent>(entity, out var body) && body.CanCollide && body.Hard &&
                ((body.CollisionMask & layer) != 0 || (body.CollisionLayer & mask) != 0))
            {
                return false;
            }
        }

        return true;
    }
}
