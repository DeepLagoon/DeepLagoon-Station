using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using System.Numerics;

namespace Content.Shared._Mono.ShipRepair;

public abstract partial class SharedShipRepairSystem : EntitySystem
{
    private List<DoAfterId> _toRemoveIds = new();

    private void InitTool()
    {
        SubscribeLocalEvent<ShipRepairToolComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<ShipRepairToolComponent, ShipRepairDoAfterEvent>(OnRepairDoAfter);
    }

    private void OnAfterInteract(Entity<ShipRepairToolComponent> ent, ref AfterInteractEvent args)
    {
        if (!args.CanReach)
            return;

        var ourXform = Transform(ent);
        var clickPos = args.ClickLocation;
        var clickWorld = _transform.ToWorldPosition(clickPos);
        var grids = new List<Entity<MapGridComponent>>();
        _mapMan.FindGridsIntersecting(ourXform.MapID, Box2.CenteredAround(clickWorld, new Vector2(1.5f, 1.5f)), ref grids, false, false);
        if (grids.Count == 0 && ourXform.GridUid == null)
            return;

        var targetGrid = ourXform.GridUid == null ? grids[0] : (ourXform.GridUid.Value, Comp<MapGridComponent>(ourXform.GridUid.Value));

        if (TryComp<ShipRepairRestrictComponent>(targetGrid, out var restrict)
            && _whitelist.IsWhitelistFail(restrict.ToolWhitelist, ent))
        {
            _popup.PopupClient(Loc.GetString("ship-repair-tool-fail-whitelist"), ent, args.User, PopupType.MediumCaution);
            return;
        }

        if (!TryComp<ShipRepairDataComponent>(targetGrid, out var repairData))
        {
            _popup.PopupClient(Loc.GetString("ship-repair-tool-no-data"), ent, args.User, PopupType.MediumCaution);
            return;
        }

        var gridClick = _transform.WithEntityId(clickPos, targetGrid);
        var gridIndices = _map.CoordinatesToTile(targetGrid, targetGrid.Comp, gridClick);

        if (!TryGetChunk(repairData, gridIndices, out var chunk))
            return;

        _toRemoveIds.Clear();
        foreach (var id in ent.Comp.DoAfters)
        {
            if (!_doAfter.IsRunning(id))
                _toRemoveIds.Add(id);
        }
        foreach (var id in _toRemoveIds)
            ent.Comp.DoAfters.Remove(id);

        // first try repair tile if we can
        if (ent.Comp.EnableTileRepair)
        {
            var relativeIndices = GetRelativeIndices(gridIndices, repairData.ChunkSize);
            var index = relativeIndices.X + relativeIndices.Y * repairData.ChunkSize;

            var storedTile = chunk.Tiles[index];
            var currentTile = _map.GetTileRef(targetGrid, targetGrid.Comp, gridIndices).Tile;

            if (storedTile != currentTile.TypeId)
            {
                StartRepair(ent, args.User, targetGrid, gridIndices, ent.Comp.TileRepairTime * ent.Comp.RepairTimeMultiplier, ent.Comp.TileRepairCost);
                return; // do not attempt anything else
            }
        }

        var alreadyExists = false;
        var notEnoughCharges = false;
        var mostCharges = 0;
        // try entity repair if we haven't done tile repair
        if (ent.Comp.EnableEntityRepair)
        {
            foreach (var (id, spec) in chunk.Entities)
            {
                // just fail the repair if it doesn't have the comp
                if (!_proto.TryIndex(repairData.EntityPalette[spec.ProtoIndex], out var entProto)
                    || !entProto.TryComp<ShipRepairableComponent>(out var repairable, Factory)
                    || entProto.TryComp<ShipRepairableRestrictComponent>(out var entRestrict, Factory)
                        && _whitelist.IsWhitelistFail(entRestrict.ToolWhitelist, ent)
                )
                    continue;

                var delay = repairable.RepairTime * ent.Comp.RepairTimeMultiplier;
                var cost = repairable.RepairCost;

                // only consider it if it's close enough
                if ((spec.LocalPosition - gridClick.Position).Length() > ent.Comp.EntitySearchRadius)
                    continue;

                var needsRepair = true;
                if (TryGetOriginal(spec, out var origUid))
                {
                    var ev = new ShipRepairReinstateQueryEvent(true);
                    RaiseLocalEvent(origUid, ref ev);

                    if (!ev.Handled && !CanRestoreFragment(Transform(origUid), targetGrid)
                        && !IsLooseDebris(Transform(origUid)))
                    {
                        alreadyExists = true;
                        continue;
                    }
                    needsRepair = ev.Repairable;
                }

                // Each tool may still have actions started by another user.
                var hasIdentical = false;
                foreach (var doAfterId in ent.Comp.DoAfters)
                {
                    if (!TryComp<DoAfterComponent>(doAfterId.Uid, out var doAfterComp)
                        || !doAfterComp.DoAfters.TryGetValue(doAfterId.Index, out var doAfter)
                        || doAfter.Args.Event is not ShipRepairDoAfterEvent repairEv)
                        continue;

                    if ((repairEv.TargetGrid == null || repairEv.TargetGrid == targetGrid.Owner)
                        && repairEv.TargetGridIndices == gridIndices && repairEv.RepairId == id)
                    {
                        hasIdentical = true;
                        break;
                    }
                }

                if (hasIdentical)
                    continue;

                var enough = !_charges.HasInsufficientCharges(ent, cost);
                notEnoughCharges |= !enough;
                if (needsRepair && enough)
                {
                    StartRepair(ent, args.User, targetGrid, gridIndices, delay, cost, id);
                    return;
                }

                if (cost > mostCharges)
                    mostCharges = cost;
            }
        }
        if (notEnoughCharges)
            _popup.PopupClient(Loc.GetString("ship-repair-tool-insufficient-ammo", ("cost", mostCharges)), ent, args.User);
        else if (alreadyExists && _net.IsServer && ent.Comp.CheckPreExistingEntities) // else we show it once or twice depending on whether it's in PVS
            _popup.PopupEntity(Loc.GetString("ship-repair-tool-entity-exists"), ent, args.User, PopupType.SmallCaution);
    }

    private void StartRepair(Entity<ShipRepairToolComponent> tool, EntityUid user, Entity<MapGridComponent> grid, Vector2i tileIndices, float delay, int cost, int? repairId = null)
    {
        var ev = new ShipRepairDoAfterEvent
        {
            TargetGridIndices = tileIndices,
            RepairId = repairId,
            Cost = cost,
            TargetGrid = grid
        };

        var args = new DoAfterArgs(EntityManager, user, delay, ev, tool)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            MovementThreshold = 0.5f,
            // only block if we're trying the exact same
            DuplicateCondition = DuplicateConditions.SameEvent
        };

        if (_doAfter.TryStartDoAfter(args, out var id))
        {
            if (_doAfter.IsRunning(id))
                tool.Comp.DoAfters.Add(id.Value);
            _audio.PlayPredicted(tool.Comp.RepairSound, tool, user);
            // we don't need to spawn it on server, however this makes serverside failures make the effect anyway
            if (_net.IsClient && _timing.IsFirstTimePredicted)
            {
                // needed so it doesn't fall off if in space
                var effect = Spawn(tool.Comp.ConstructEffect, new EntityCoordinates(grid, Vector2.Zero));
                _parent.SetForceParent(effect, new EntityCoordinates(grid, _map.TileCenterToVector(grid, tileIndices)));
            }
        }
    }

    private void OnRepairDoAfter(Entity<ShipRepairToolComponent> ent, ref ShipRepairDoAfterEvent args)
    {
        ent.Comp.DoAfters.Remove(args.DoAfter.Id);

        if (args.Cancelled || args.Handled)
            return;

        if (args.TargetGrid is not { } targetGrid || !TryComp<ShipRepairDataComponent>(targetGrid, out var repairData))
            return;

        if (!TryGetChunk(repairData, args.TargetGridIndices, out var chunk))
            return;

        if (_charges.HasInsufficientCharges(ent, args.Cost))
        {
            _popup.PopupEntity(Loc.GetString("ship-repair-tool-insufficient-ammo", ("cost", args.Cost)), ent, args.User);
            return;
        }

        if (args.RepairId != null)
        {
            if (_net.IsClient || !chunk.Entities.TryGetValue(args.RepairId.Value, out var spec))
                return;

            if (!TryComp<MapGridComponent>(targetGrid, out var grid))
                return;

            EntityUid? looseDebris = null;
            if (TryGetOriginal(spec, out var origUid))
            {
                var ev = new ShipRepairReinstateQueryEvent(true);
                RaiseLocalEvent(origUid, ref ev);
                if (!ev.Handled)
                {
                    var original = Transform(origUid);
                    if (CanRestoreFragment(original, targetGrid))
                    {
                        if (TryRestoreFragment(origUid, original, (targetGrid, grid), args.TargetGridIndices, spec))
                        {
                            _charges.UseCharges(ent, args.Cost);
                            args.Handled = true;
                        }

                        return;
                    }

                    if (IsLooseDebris(original))
                        looseDebris = origUid;
                    else if (ent.Comp.CheckPreExistingEntities)
                        return;
                }
                else if (!ev.Repairable && ent.Comp.CheckPreExistingEntities)
                    return;
            }

            var protoId = repairData.EntityPalette[spec.ProtoIndex];
            var coords = new EntityCoordinates(targetGrid, spec.LocalPosition);

            var spawned = Spawn(protoId, coords);
            _transform.SetLocalRotation(spawned, spec.Rotation);
            if (looseDebris is { } debris)
                QueueDel(debris);

            spec.OriginalEntity = GetNetEntity(spawned);

            var dirtMsg = new RepairEntityMessage(GetNetEntity(targetGrid), args.TargetGridIndices, args.RepairId.Value, spec);
            RaiseNetworkEvent(dirtMsg);
        }
        else
        {
            TryRepairTileTile((targetGrid, repairData), args.TargetGridIndices);
        }

        _charges.UseCharges(ent, args.Cost);
        args.Handled = true;
    }

    private bool TryGetOriginal(ShipRepairEntitySpecifier spec, out EntityUid original)
    {
        original = default;
        if (spec.OriginalEntity is not { } netEntity || !TryGetEntity(netEntity, out var uid)
            || TerminatingOrDeleted(uid.Value))
            return false;

        original = uid.Value;
        return true;
    }

    private static bool IsLooseDebris(TransformComponent transform) =>
        transform.GridUid == null && transform.ParentUid == transform.MapUid;

    private bool CanRestoreFragment(TransformComponent original, EntityUid targetGrid)
    {
        return original.Anchored && original.GridUid is { } fragment && fragment != targetGrid
            && original.ParentUid == fragment
            && original.MapUid == Transform(targetGrid).MapUid
            && TryComp<ShipRepairFragmentComponent>(fragment, out var marker)
            && marker.SourceGrids.Contains(GetNetEntity(targetGrid));
    }

    private bool TryRestoreFragment(EntityUid original, TransformComponent transform, Entity<MapGridComponent> target,
        Vector2i indices, ShipRepairEntitySpecifier spec)
    {
        if (!_map.TryGetTileRef(target, target.Comp, indices, out var tile) || tile.Tile.IsEmpty)
            return false;

        var metadata = MetaData(original);
        var proto = metadata.EntityPrototype;
        foreach (var entity in _map.GetAnchoredEntities(target, indices))
        {
            if (MetaData(entity).EntityPrototype == proto &&
                Vector2.DistanceSquared(Transform(entity).LocalPosition, spec.LocalPosition) < 0.01f)
                return false;
        }

        var previous = transform.Coordinates;
        var rotation = transform.LocalRotation;
        _transform.Unanchor(original, transform);
        _transform.SetCoordinates((original, transform, metadata), new EntityCoordinates(target, spec.LocalPosition), spec.Rotation);
        if (_transform.AnchorEntity((original, transform, metadata), target))
            return true;

        _transform.SetCoordinates((original, transform, metadata), previous, rotation);
        _transform.AnchorEntity((original, transform, metadata));
        return false;
    }
}
