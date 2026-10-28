using System.Linq;
using Content.Server.Atmos.Components;
using Content.Shared.Atmos;
using Robust.Shared.Map.Components;

namespace Content.Server.Atmos.EntitySystems;

public sealed partial class AtmosphereSystem
{
    public void SetSafeGridAtmosphere(EntityUid uid)
    {
        if (!TryComp<MapGridComponent>(uid, out var grid)) return;
        var atmosphere = EnsureComp<GridAtmosphereComponent>(uid);
        atmosphere.Simulated = false;
        var air = new GasMixture(GetVolumeForTiles(grid)) { Temperature = Atmospherics.T20C };
        air.SetMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard * air.Volume / Atmospherics.CellVolume);
        air.SetMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard * air.Volume / Atmospherics.CellVolume);
        var configure = new SafeGridAtmosphereEvent(air);
        RaiseLocalEvent(uid, ref configure);
        air.MarkImmutable();
        var positions = new HashSet<Vector2i>();
        foreach (var floor in _mapSystem.GetAllTiles(uid, grid))
        {
            positions.Add(floor.GridIndices);
            var tile = GetOrNewTile(uid, atmosphere, floor.GridIndices, invalidateNew: false);
            tile.Air = air;
            tile.Space = false;
            tile.AirArchived = null;
        }
        foreach (var position in atmosphere.Tiles.Keys.ToArray())
            if (!positions.Contains(position)) atmosphere.Tiles.Remove(position);
        atmosphere.InvalidatedCoords.Clear();
    }
}

/// <summary>Allows grid-specific controllers to configure safe air before it becomes immutable.</summary>
[ByRefEvent]
public readonly record struct SafeGridAtmosphereEvent(GasMixture Air);
