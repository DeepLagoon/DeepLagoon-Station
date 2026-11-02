using System.Linq;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Doors.Components;
using Robust.Shared.Containers;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    private void ContainResidents()
    {
        foreach (var visit in _visits.Values)
        {
            if (!Exists(visit.Body) || !Exists(visit.Apartment.Grid)) continue;
            var transform = Transform(visit.Body);
            var position = System.Numerics.Vector2.Transform(_transform.GetWorldPosition(visit.Body), Transform(visit.Apartment.Grid).InvWorldMatrix);
            var cell = new Vector2i((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y));
            if (transform.GridUid == visit.Apartment.Grid && !visit.Apartment.Exterior.Contains(cell) &&
                cell.X >= 0 && cell.Y >= 0 && cell.X < visit.Apartment.Template.Width && cell.Y < visit.Apartment.Template.Height) continue;
            _transform.SetCoordinates(visit.Body, ArrivalCoordinates(visit.Apartment, false));
        }
    }

    private Container FurnitureStorage(Instance instance) =>
        _containers.EnsureContainer<Container>(instance.Grid, "apartment-furniture-storage");

    private void StoreFurniture(Instance instance, EntityUid entity)
    {
        var identity = EnsureComp<ApartmentFurnitureIdentityComponent>(entity);
        identity.StoredRotation = ((int)MathF.Round((float)Transform(entity).LocalRotation.Degrees / 90) % 4 + 4) % 4;
        _transform.Unanchor(entity);
        if (!_containers.Insert(entity, FurnitureStorage(instance)))
            throw new InvalidOperationException("Cannot store apartment furniture.");
        identity.Stored = true;
    }

    private List<ApartmentPlacement> StoredFurniture(Instance instance) => instance.Furniture
        .Where(pair => Exists(pair.Value) && TryComp<ApartmentFurnitureIdentityComponent>(pair.Value, out var identity) && identity.Stored)
        .Select(pair => new ApartmentPlacement { Id = pair.Key,
            Furniture = Comp<ApartmentFurnitureIdentityComponent>(pair.Value).Furniture,
            Rotation = Comp<ApartmentFurnitureIdentityComponent>(pair.Value).StoredRotation }).ToList();

    // The authored non-space tiles define the apartment, including maps whose
    // perimeter uses railings or other non-airtight structures.
    private HashSet<Vector2i> ApartmentShell(Instance instance)
    {
        var floors = _map.GetAllTiles(instance.Grid, Comp<Robust.Shared.Map.Components.MapGridComponent>(instance.Grid))
            .Where(tile => !tile.Tile.IsEmpty).Select(tile => tile.GridIndices).ToHashSet();
        instance.Exterior.Clear();
        for (var x = -1; x <= instance.Template.Width; x++)
        for (var y = -1; y <= instance.Template.Height; y++)
            if (!floors.Contains(new Vector2i(x, y))) instance.Exterior.Add(new Vector2i(x, y));
        var directions = new[] { new Vector2i(1, 0), new Vector2i(-1, 0), new Vector2i(0, 1), new Vector2i(0, -1) };
        var shell = new HashSet<Vector2i>();
        var children = Transform(instance.Grid).ChildEnumerator;
        while (children.MoveNext(out var uid))
        {
            var transform = Transform(uid);
            if (MetaData(uid).EntityPrototype is not { } prototype) continue;
            if (!_tags.HasTag(uid, "Wall") && !_tags.HasTag(uid, "Window") &&
                !HasComp<DoorComponent>(uid) && !prototype.Components.ContainsKey("Airtight") && !prototype.Components.ContainsKey("Window")) continue;
            var cell = new Vector2i((int)MathF.Floor(transform.LocalPosition.X), (int)MathF.Floor(transform.LocalPosition.Y));
            if (!floors.Contains(cell) || directions.Any(direction => !floors.Contains(cell + direction))) shell.Add(cell);
        }
        return shell;
    }

    private bool IsApartmentShell(Instance instance, EntityUid uid, Vector2i cell) =>
        instance.Shell.Contains(cell) && (HasComp<DoorComponent>(uid) || _tags.HasTag(uid, "Wall") || _tags.HasTag(uid, "Window") ||
            (MetaData(uid).EntityPrototype?.Components.ContainsKey("Airtight") == true || MetaData(uid).EntityPrototype?.Components.ContainsKey("Window") == true));
}
