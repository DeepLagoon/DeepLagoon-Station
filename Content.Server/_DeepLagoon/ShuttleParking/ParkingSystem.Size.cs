using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private readonly record struct ParkingHull(int Tiles, float Width, float Height);
    private readonly Dictionary<ResPath, ParkingHull> _stockParkingHulls = new();

    public int GetCategory(EntityUid grid)
    {
        var hull = GetParkingHull(grid, Comp<MapGridComponent>(grid));
        // Parking provenance survives storage and also covers legacy ships registered on first use.
        var vesselId = TryComp<ParkingShuttleComponent>(grid, out var ship) ? ship.Vessel : "";
        if (string.IsNullOrEmpty(vesselId) && TryComp<VesselComponent>(grid, out var vessel))
            vesselId = vessel.VesselId.Id;

        if (!_prototypes.TryIndex<VesselPrototype>(vesselId, out var prototype))
            return ParkingRules.Category(hull.Tiles, hull.Width, hull.Height);

        var category = prototype.Category switch
        {
            VesselSize.Micro or VesselSize.Small => 0,
            VesselSize.Medium => 1,
            VesselSize.Large => 2,
            _ => throw new InvalidOperationException("У шаттла не указана категория верфи."),
        };
        var stock = GetStockParkingHull(prototype.ShuttlePath);
        return ParkingRules.VesselCategory(category, hull.Tiles, hull.Width, hull.Height,
            stock.Tiles, stock.Width, stock.Height);
    }

    private ParkingHull GetStockParkingHull(ResPath path)
    {
        if (_stockParkingHulls.TryGetValue(path, out var cached))
            return cached;

        // Use the engine's tile deserializer without spawning a second shuttle or its equipment.
        // Read packaged content directly: a user-data export must not replace the catalog baseline.
        using var stream = _resources.ContentFileRead(path);
        using var reader = new StreamReader(stream);
        var data = (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;
        var migrations = new BeforeEntityReadEvent();
        RaiseLocalEvent(migrations);
        var context = new EntityDeserializer(_dependencies, data, DeserializationOptions.Default,
            migrations.RenamedPrototypes, migrations.DeletedPrototypes);
        if (!context.TryProcessData())
            throw new InvalidDataException($"Не удалось прочитать исходный корпус шаттла: {path}");

        var grids = context.YamlEntities.Values
            .Where(entity => entity.Components?.ContainsKey("MapGrid") == true).ToArray();
        if (grids.Length != 1)
            throw new InvalidDataException($"В исходной карте шаттла должен быть один грид: {path}");

        var component = _dependencies.Resolve<ISerializationManager>()
            .Read<MapGridComponent>(grids[0].Components!["MapGrid"], context, notNullableOverride: true);
        var hull = GetParkingHull(EntityUid.Invalid, component);
        _stockParkingHulls.Add(path, hull);
        return hull;
    }

    private ParkingHull GetParkingHull(EntityUid uid, MapGridComponent grid)
    {
        var count = 0;
        var min = new Vector2i(int.MaxValue, int.MaxValue);
        var max = new Vector2i(int.MinValue, int.MinValue);
        foreach (var tile in _map.GetAllTiles(uid, grid))
        {
            count++;
            min = Vector2i.ComponentMin(min, tile.GridIndices);
            max = Vector2i.ComponentMax(max, tile.GridIndices);
        }
        if (count == 0)
            throw new InvalidOperationException("У шаттла нет корпуса для сохранения.");
        return new(count, (max.X - min.X + 1) * grid.TileSize, (max.Y - min.Y + 1) * grid.TileSize);
    }
}
