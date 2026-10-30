using System.Linq;

namespace Content.Shared._DeepLagoon.Apartments;

/// <summary>Pure layout rules, shared by the server and preview. No spawning, wallet or storage dependencies.</summary>
public static class ApartmentLayout
{
    public const int SafetyLimit = 400;
    public static List<ApartmentPlacement> Resolve(ApartmentTemplatePrototype template, ApartmentDelta delta,
        IEnumerable<ApartmentPlacement>? mapFurniture = null)
    {
        if (delta.SchemaVersion != 1 || delta.Template != template.ID || delta.TemplateVersion != template.Version)
            throw new InvalidOperationException("Apartment template needs migration; the saved patch was not overwritten.");
        var baseline = template.Furniture.Concat(mapFurniture ?? []).ToArray();
        var result = baseline.Where(p => !delta.Removed.Contains(p.Id)).ToDictionary(p => p.Id, p => p.Copy());
        foreach (var change in delta.Changed)
        {
            if (!result.TryGetValue(change.Id, out var original) || original.Furniture != change.Furniture)
                throw new InvalidOperationException("Invalid template furniture override.");
            result[change.Id] = change.Copy();
        }
        foreach (var addition in delta.Added)
        {
            if (baseline.Any(p => p.Id == addition.Id) || !result.TryAdd(addition.Id, addition.Copy()))
                throw new InvalidOperationException("Duplicate apartment furniture ID.");
        }
        return result.Values.ToList();
    }

    public static ApartmentDelta Diff(ApartmentTemplatePrototype template, IReadOnlyList<ApartmentPlacement> layout, int revision,
        IEnumerable<ApartmentPlacement>? mapFurniture = null)
    {
        var baseline = template.Furniture.Concat(mapFurniture ?? []).ToDictionary(p => p.Id);
        return new ApartmentDelta
        {
            Template = template.ID, TemplateVersion = template.Version, Revision = revision,
            Removed = baseline.Keys.Where(id => layout.All(p => p.Id != id)).ToList(),
            Changed = layout.Where(p => baseline.TryGetValue(p.Id, out var original) && !p.SameAs(original)).Select(p => p.Copy()).ToList(),
            Added = layout.Where(p => !baseline.ContainsKey(p.Id)).Select(p => p.Copy()).ToList(),
        };
    }

    public static IEnumerable<Vector2i> Cells(ApartmentPlacement placement, ApartmentFurniturePrototype furniture)
    {
        var size = placement.Rotation % 2 == 0 ? furniture.Size : new Vector2i(furniture.Size.Y, furniture.Size.X);
        for (var x = 0; x < size.X; x++)
        for (var y = 0; y < size.Y; y++)
            yield return new Vector2i(placement.X + x, placement.Y + y);
    }

    public static string? Validate(ApartmentTemplatePrototype template, IReadOnlyList<ApartmentPlacement> layout,
        Func<string, ApartmentFurniturePrototype?> catalog, ApartmentRoomScene? scene = null,
        IEnumerable<ApartmentPlacement>? mapFurniture = null, Func<string, int>? stock = null, bool validateStock = true)
    {
        var mapped = (mapFurniture ?? []).ToDictionary(p => p.Id);
        if (layout.Count > SafetyLimit) return "Не удалось разместить мебель: слишком много объектов.";
        if (mapped.Keys.Any(id => layout.All(p => p.Id != id))) return "Исходный интерьер можно перемещать, но нельзя удалять.";
        var ids = new HashSet<string>();
        var occupied = new Dictionary<Vector2i, bool>();
        var blocked = new HashSet<Vector2i>();
        var counts = new Dictionary<string, int>();
        var floors = scene?.Tiles.Select(tile => new Vector2i((int) tile.X, (int) tile.Y)).ToHashSet();
        foreach (var p in layout)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || !ids.Add(p.Id)) return "Некорректный или повторяющийся ID предмета.";
            if (p.Rotation is < 0 or > 3) return "Некорректный поворот предмета.";
            if ((!template.Catalog.Any(id => id.ToString() == p.Furniture) &&
                 (!mapped.TryGetValue(p.Id, out var mappedOriginal) || mappedOriginal.Furniture != p.Furniture)) || catalog(p.Furniture) is not { } furniture)
                return "Предмет отсутствует в каталоге квартиры.";
            if (!furniture.CanRotate && p.Rotation != 0) return "Этот предмет нельзя поворачивать.";
            var original = template.Furniture.FirstOrDefault(item => item.Id == p.Id);
            if (original != null && original.Furniture != p.Furniture) return "Нельзя подменить предмет шаблона.";
            if (mapped.TryGetValue(p.Id, out var mapOriginal) && mapOriginal.Furniture != p.Furniture) return "Нельзя подменить предмет шаблона.";
            var unchangedMapPlacement = mapped.TryGetValue(p.Id, out var baseline) && p.SameAs(baseline);
            counts[furniture.StockKey] = counts.GetValueOrDefault(furniture.StockKey) + 1;
            if (validateStock && counts[furniture.StockKey] > (furniture.ExistingOnly ? 1 : stock?.Invoke(furniture.StockKey) ?? furniture.MaxCount))
                return "Недостаточно доступных экземпляров мебели. Откройте ещё один набор в обустройстве.";
            foreach (var cell in Cells(p, furniture))
            {
                if (floors != null ? !floors.Contains(cell) : cell.X < 1 || cell.Y < 1 || cell.X >= template.Width - 1 || cell.Y >= template.Height - 1)
                    return "Предмет пересекается со стеной.";
                if (furniture.Layer == "Wall" && scene?.Walls.Contains(cell) != true) return "Настенный декор нужно разместить на стене.";
                if (!unchangedMapPlacement && scene?.Occupied.Contains(cell) == true && (furniture.Layer != "Wall" || !scene.Walls.Contains(cell))) return "Место занято стационарным объектом квартиры.";
                if (template.ReservedCells.Contains(cell)) return "Площадку лифта и коридор нельзя занимать мебелью.";
                if (cell == template.Arrival || scene == null && cell == template.Arrival + new Vector2i(0, 1))
                    return "Площадка прибытия и терминал должны оставаться свободными.";
                if (!furniture.AllowsOverlap)
                {
                    // Preserve authored map overlaps, while moved or added objects still require a free cell.
                    if (occupied.TryGetValue(cell, out var previousUnchanged) && !(previousUnchanged && unchangedMapPlacement))
                        return "Предметы пересекаются.";
                    occupied[cell] = unchangedMapPlacement;
                }
                if (furniture.BlocksMovement) blocked.Add(cell);
            }
        }
        // A valid plan must not wall off any free floor from the independent exit terminal.
        var directions = new[] { new Vector2i(1, 0), new Vector2i(-1, 0), new Vector2i(0, 1), new Vector2i(0, -1) };
        HashSet<Vector2i> Reachable(HashSet<Vector2i> obstacles)
        {
            var result = new HashSet<Vector2i> { template.Arrival };
            var queue = new Queue<Vector2i>();
            queue.Enqueue(template.Arrival);
            while (queue.TryDequeue(out var cell))
            foreach (var direction in directions)
            {
                var next = cell + direction;
                if (floors != null ? !floors.Contains(next) : next.X < 1 || next.Y < 1 || next.X >= template.Width - 1 || next.Y >= template.Height - 1) continue;
                if (obstacles.Contains(next) || scene?.Blocked.Contains(next) == true) continue;
                if (result.Add(next)) queue.Enqueue(next);
            }
            return result;
        }
        var reachable = Reachable(blocked);
        var baselineBlocked = mapped.Values.Where(p => catalog(p.Furniture)?.BlocksMovement == true)
            .SelectMany(p => Cells(p, catalog(p.Furniture)!)).ToHashSet();
        foreach (var cell in Reachable(baselineBlocked))
            if (!blocked.Contains(cell) && !reachable.Contains(cell))
                return "Мебель перекрывает проход к выходу.";
        return null;
    }
}
