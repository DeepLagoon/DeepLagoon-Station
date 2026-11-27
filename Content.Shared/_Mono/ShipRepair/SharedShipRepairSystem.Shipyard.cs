using System.Linq;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.DoAfter;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Shared._Mono.ShipRepair;

public abstract partial class SharedShipRepairSystem
{
    public bool HasActiveToolRepairs(EntityUid grid)
    {
        var tools = EntityQueryEnumerator<ShipRepairToolComponent>();
        while (tools.MoveNext(out var tool))
            foreach (var id in tool.DoAfters)
                if (_doAfter.IsRunning(id) && TryComp<DoAfterComponent>(id.Uid, out var actions) &&
                    actions.DoAfters.TryGetValue(id.Index, out var action) && action.Args.Event is ShipRepairDoAfterEvent repair &&
                    (repair.TargetGrid == null || repair.TargetGrid == grid)) return true;
        return false;
    }
    /// <summary>The same blueprint and existence rules as the handheld repair device.</summary>
    public List<ParkingRepairPart> GetShipyardRepairs(EntityUid uid)
    {
        var result = new List<ParkingRepairPart>();
        if (!TryComp<ShipRepairDataComponent>(uid, out var data) || !TryComp<MapGridComponent>(uid, out var grid)) return result;
        foreach (var (indices, chunk) in data.Chunks.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y))
        {
            for (var i = 0; i < chunk.Tiles.Length; i++)
            {
                var tile = chunk.Tiles[i];
                var position = indices * data.ChunkSize + new Vector2i(i % data.ChunkSize, i / data.ChunkSize);
                if (tile == Tile.Empty.TypeId || _map.GetTileRef(uid, grid, position).Tile.TypeId == tile) continue;
                result.Add(new($"t:{position.X}:{position.Y}", "Палуба", "", _map.TileCenterToVector((uid, grid), position), Angle.Zero, tile, "tiles", .5, 1));
            }
            foreach (var (id, spec) in chunk.Entities.OrderBy(p => p.Key))
            {
                if (spec.ProtoIndex < 0 || spec.ProtoIndex >= data.EntityPalette.Count ||
                    !_proto.TryIndex(data.EntityPalette[spec.ProtoIndex], out var proto) ||
                    Content.Shared._DeepLagoon.Mining.LaserDrillAvailability.IsRetiredPrototype(_proto, proto.ID) ||
                    !proto.TryComp<ShipRepairableComponent>(out var repairable, Factory)) continue;
                EntityUid? originalEntity = null;
                EntityUid? sourceGrid = null;
                if (TryGetOriginal(spec, out var original))
                {
                    var transform = Transform(original);
                    var fragment = transform.GridUid;
                    var ownFragment = transform.Anchored && fragment is { } source && source != uid && transform.ParentUid == source &&
                        TryComp<ShipRepairFragmentComponent>(source, out var marker) && marker.SourceGrids.Contains(GetNetEntity(uid));
                    if (!ownFragment && !IsLooseDebris(transform)) continue;
                    originalEntity = original;
                    sourceGrid = fragment;
                }
                var position = _map.LocalToTile(uid, grid, new EntityCoordinates(uid, spec.LocalPosition));
                if (_map.GetAnchoredEntities((uid, grid), position).Any(e => MetaData(e).EntityPrototype == proto &&
                    (Transform(e).LocalPosition - spec.LocalPosition).LengthSquared() < .01f)) continue;
                result.Add(new($"e:{indices.X}:{indices.Y}:{id}", proto.Name, proto.ID, spec.LocalPosition, spec.Rotation, 0,
                    proto.ID.Contains("Wall", StringComparison.Ordinal) || proto.ID.Contains("Window", StringComparison.Ordinal) ? "structures" : "equipment", repairable.RepairTime,
                    originalEntity != null ? 0 : repairable.RepairCost, originalEntity is { } orig ? GetNetEntity(orig) : null,
                    sourceGrid is { } sourceUid ? GetNetEntity(sourceUid) : null));
            }
        }
        return result;
    }

    /// <summary>Caller validates the entire plan before the first mutation. Tiles must precede equipment.</summary>
    public EntityUid? ApplyShipyardRepair(EntityUid uid, ParkingRepairPart part)
    {
        var data = Comp<ShipRepairDataComponent>(uid);
        if (part.Kind == "tiles")
        {
            var indices = _map.LocalToTile(uid, Comp<MapGridComponent>(uid), new EntityCoordinates(uid, part.Position));
            if (!TryRepairTileTile((uid, data), indices)) throw new InvalidOperationException("Repair tile disappeared.");
            return null;
        }
        var fields = part.Id.Split(':');
        var chunk = data.Chunks[new Vector2i(int.Parse(fields[1]), int.Parse(fields[2]))];
        var spec = chunk.Entities[int.Parse(fields[3])];
        if (Content.Shared._DeepLagoon.Mining.LaserDrillAvailability.IsRetiredPrototype(_proto, data.EntityPalette[spec.ProtoIndex]))
            throw new InvalidOperationException("This equipment is no longer available for restoration.");
        EntityUid entity;
        if (TryGetOriginal(spec, out var original))
        {
            if (part.Original != GetNetEntity(original) || !GetShipyardRepairs(uid).Any(p => p.Id == part.Id && p.Original == part.Original))
                throw new InvalidOperationException("Repair target already exists.");
            var transform = Transform(original);
            _transform.Unanchor(original, transform);
            _transform.SetCoordinates(original, new EntityCoordinates(uid, spec.LocalPosition));
            if (!_transform.AnchorEntity(original)) throw new InvalidOperationException("Cannot anchor repair fragment.");
            entity = original;
        }
        else
        {
            if (part.Original != null) throw new InvalidOperationException("Repair fragment disappeared.");
            entity = Spawn(data.EntityPalette[spec.ProtoIndex], new EntityCoordinates(uid, spec.LocalPosition));
        }
        _transform.SetLocalRotation(entity, spec.Rotation);
        spec.OriginalEntity = GetNetEntity(entity);
        Dirty(uid, data);
        return entity;
    }
}
