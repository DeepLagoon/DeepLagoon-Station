using System.Linq;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Doors.Components;
using Content.Shared.Physics;
using Content.Shared.Item;
using Content.Shared.Wall;
using Content.Shared.Tag;
using Robust.Shared.EntitySerialization.Components;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    [Dependency] private TagSystem _tags = default!;

    private IEnumerable<ApartmentMapFurniture> EditableMapFurniture(Instance instance) => instance.MapFurniture.Values.Where(item =>
        instance.Furniture.TryGetValue(item.Baseline.Id, out var entity) && Exists(entity) && Transform(entity).ParentUid == instance.Grid);

    private IEnumerable<ApartmentPlacement> MapBaseline(Instance instance) => EditableMapFurniture(instance).Select(item => item.Baseline);

    private ApartmentFurniturePrototype? Catalog(Instance instance, string id) =>
        instance.MapFurniture.TryGetValue(id, out var mapped) ? mapped.Definition() : Catalog(id);

    private bool IsWallFixture(EntityUid uid) => HasComp<WallMountComponent>(uid) || HasComp<Content.Shared.Light.Components.PoweredLightComponent>(uid)
        || TryComp<PlacementReplacementComponent>(uid, out var replacement) && replacement.Key == "lights";

    private void RegisterMapFurniture(Instance instance)
    {
        var children = Transform(instance.Grid).ChildEnumerator;
        while (children.MoveNext(out var uid))
        {
            var transform = Transform(uid);
            var cell = new Vector2i((int) MathF.Floor(transform.LocalPosition.X), (int) MathF.Floor(transform.LocalPosition.Y));
            if (!transform.Anchored || !TryComp<YamlUidComponent>(uid, out var yaml) ||
                HasComp<DoorComponent>(uid) || HasComp<ApartmentTerminalComponent>(uid) || HasComp<ApartmentElevatorComponent>(uid) ||
                IsWallFixture(uid) || _tags.HasTag(uid, "Wall") ||
                instance.Template.ReservedCells.Contains(cell) || cell == instance.Template.Arrival) continue;
            var metadata = MetaData(uid);
            if (metadata.EntityPrototype is not { } prototype || prototype.Components.ContainsKey("PoweredLight") || prototype.Components.ContainsKey("Window")) continue;
            var id = "map_" + yaml.Uid;
            instance.MapFurniture.Add(id, new ApartmentMapFurniture
            {
                Baseline = new ApartmentPlacement { Id = id, Furniture = id, X = cell.X, Y = cell.Y,
                    Rotation = ((int) MathF.Round((float) transform.LocalRotation.Degrees / 90) % 4 + 4) % 4 },
                Prototype = prototype.ID, Name = metadata.EntityName,
                BlocksMovement = TryComp<PhysicsComponent>(uid, out var physics) && physics.CanCollide && physics.Hard && (physics.CollisionLayer & (int) CollisionGroup.Impassable) != 0 && !HasComp<ItemComponent>(uid),
                AllowsOverlap = HasComp<ItemComponent>(uid) || prototype.Components.ContainsKey("Microwave"),
                CanRotate = !transform.NoLocalRotation
            });
            instance.Furniture.Add(id, uid);
            var identity = EnsureComp<ApartmentFurnitureIdentityComponent>(uid);
            identity.Id = identity.Furniture = id;
        }
    }

    private void MoveMapFurniture(Instance instance, ApartmentPlacement placement)
    {
        var entity = instance.Furniture[placement.Id];
        _transform.Unanchor(entity);
        _transform.SetCoordinates(entity, new EntityCoordinates(instance.Grid, new Vector2(placement.X + .5f, placement.Y + .5f)));
        _transform.SetLocalRotation(entity, Angle.FromDegrees(placement.Rotation * 90));
        _transform.AnchorEntity(entity);
    }

    private List<ApartmentPlacement> CurrentLayout(Instance instance)
    {
        var layout = new List<ApartmentPlacement>();
        var definitions = instance.Template.Furniture.Concat(instance.MapFurniture.Values.Select(item => item.Baseline))
            .Concat(instance.Delta.Added).Concat(instance.Delta.Changed).GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.Last());
        foreach (var (id, entity) in instance.Furniture)
        {
            if (!Exists(entity)) continue;
            var transform = Transform(entity);
            if (transform.ParentUid != instance.Grid) continue;
            var furniture = TryComp<ApartmentFurnitureIdentityComponent>(entity, out var identity) && identity.Furniture.Length > 0
                ? identity.Furniture : definitions.GetValueOrDefault(id)?.Furniture;
            if (furniture == null) continue;
            var current = new ApartmentPlacement { Id = id, Furniture = furniture };
            current.X = (int) MathF.Floor(transform.LocalPosition.X);
            current.Y = (int) MathF.Floor(transform.LocalPosition.Y);
            current.Rotation = ((int) MathF.Round((float) transform.LocalRotation.Degrees / 90) % 4 + 4) % 4;
            layout.Add(current);
        }
        return layout;
    }

    private ApartmentRoomScene CaptureRoom(Instance instance)
    {
        var grid = Comp<MapGridComponent>(instance.Grid);
        var bounds = grid.LocalAABB;
        var scene = new ApartmentRoomScene
        {
            X = (int) MathF.Floor(bounds.Left), Y = (int) MathF.Floor(bounds.Bottom),
            Width = Math.Max(1, (int) MathF.Ceiling(bounds.Right) - (int) MathF.Floor(bounds.Left)),
            Height = Math.Max(1, (int) MathF.Ceiling(bounds.Top) - (int) MathF.Floor(bounds.Bottom))
        };
        foreach (var tile in _map.GetAllTiles(instance.Grid, grid))
            scene.Tiles.Add(new GridEditorItem { Id = $"t:{tile.GridIndices.X}:{tile.GridIndices.Y}", Kind = "tile",
                X = tile.GridIndices.X, Y = tile.GridIndices.Y, Prototype = _tiles[tile.Tile.TypeId].ID,
                Variant = tile.Tile.Variant, Flags = tile.Tile.Flags, Mirror = tile.Tile.RotationMirroring });
        var editable = instance.Furniture.Values.ToHashSet();
        var children = Transform(instance.Grid).ChildEnumerator;
        while (children.MoveNext(out var uid))
        {
            if (editable.Contains(uid) || HasComp<ActorComponent>(uid) || !TryComp(uid, out MetaDataComponent? metadata) ||
                metadata.EntityPrototype == null) continue;
            var transform = Transform(uid);
            if (!transform.Anchored) continue;
            scene.Objects.Add(new GridEditorItem { Id = "e:" + GetNetEntity(uid), Kind = "entity", Prototype = metadata.EntityPrototype.ID,
                X = transform.LocalPosition.X, Y = transform.LocalPosition.Y, Rotation = (float) transform.LocalRotation.Degrees, Anchored = true });
            var cell = new Vector2i((int) MathF.Floor(transform.LocalPosition.X), (int) MathF.Floor(transform.LocalPosition.Y));
            if (_tags.HasTag(uid, "Wall")) scene.Walls.Add(cell);
            if (HasComp<DoorComponent>(uid) || !IsWallFixture(uid)) scene.Occupied.Add(cell);
            if (!HasComp<DoorComponent>(uid) && TryComp<PhysicsComponent>(uid, out var physics) && physics.CanCollide && physics.Hard &&
                (physics.CollisionLayer & (int) CollisionGroup.Impassable) != 0) scene.Blocked.Add(cell);
        }
        return scene;
    }
}
