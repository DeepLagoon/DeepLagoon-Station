using System.Linq;
using Content.Server.Atmos.Components;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Maps;
using Robust.Shared.Map.Components;

namespace Content.Server.Atmos.EntitySystems;

public sealed partial class AtmosphereSystem
{
    /// <summary>Inspect live anchored blockers without rebuilding or resetting the grid atmosphere.</summary>
    public GridEditorAtmosResult InspectGridEditorAtmos(EntityUid uid, Guid token, bool fill = false)
    {
        var grid = Comp<MapGridComponent>(uid);
        var cells = new Dictionary<Vector2i, GridEditorAtmosCell>();
        foreach (var tile in _mapSystem.GetAllTiles(uid, grid))
        {
            var position = tile.GridIndices;
            var blockers = GetAirtightData(uid, grid, position);
            cells[position] = new GridEditorAtmosCell { X = position.X, Y = position.Y,
                Blocked = blockers.BlockedDirections, NoAirWhenBlocked = blockers.NoAirWhenBlocked,
                MapAtmosphere = ((ContentTileDefinition) _tileDefinitionManager[tile.Tile.TypeId]).MapAtmosphere };
        }
        GridEditorAtmosTopology.Analyze(cells);
        var sealedCells = cells.Where(pair => pair.Value.Status == GridEditorAirStatus.Sealed).Select(pair => pair.Key).ToArray();
        var filled = 0;
        if (fill && sealedCells.Length > 0)
        {
            var atmos = EnsureComp<GridAtmosphereComponent>(uid);
            var overlay = EnsureComp<GasTileOverlayComponent>(uid);
            Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent> entity =
                new(uid, atmos, overlay, grid, Transform(uid));
            TryComp(entity.Comp4.MapUid, out MapAtmosphereComponent? mapAtmos);
            var volume = GetVolumeForTiles(grid);
            var affected = sealedCells.ToHashSet();
            foreach (var position in sealedCells)
                for (var i = 0; i < Atmospherics.Directions; i++) affected.Add(position.Offset((AtmosDirection) (1 << i)));
            // Refresh only these rooms and their immediate neighbors; retain other gas mixtures and processing queues.
            foreach (var position in affected) UpdateTileData(entity, mapAtmos, GetOrNewTile(uid, atmos, position));
            foreach (var position in affected)
            {
                var tile = GetOrNewTile(uid, atmos, position);
                UpdateAdjacentTiles(entity, tile, activate: true);
                UpdateTileAir(entity, tile, volume);
            }
            foreach (var position in sealedCells)
            {
                var tile = GetOrNewTile(uid, atmos, position);
                if (tile.Air is not { Immutable: false } air) continue;
                air.Clear(); air.Volume = volume; air.Temperature = Atmospherics.T20C;
                air.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard * volume / Atmospherics.CellVolume);
                air.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard * volume / Atmospherics.CellVolume);
                tile.AirArchived = null; tile.ArchivedCycle = 0; tile.LastShare = 0;
                AddActiveTile(atmos, tile); InvalidateVisuals(entity, tile); filled++;
            }
        }
        if (TryComp<GridAtmosphereComponent>(uid, out var atmosphere))
            foreach (var (position, cell) in cells)
                if (atmosphere.Tiles.TryGetValue(position, out var tile)) cell.Pressure = tile.Air?.Pressure ?? 0;
        return new GridEditorAtmosResult(token, cells.Values.ToList(), filled);
    }
}
