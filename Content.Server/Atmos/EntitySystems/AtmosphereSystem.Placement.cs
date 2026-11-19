using Content.Server.Atmos.Components;
using Content.Shared.Atmos;

namespace Content.Server.Atmos.EntitySystems;

public sealed partial class AtmosphereSystem
{
    /// <summary>One-time air supplied by a deployed room, preserving normal subsequent atmos simulation.</summary>
    public void SetInitialTileAtmosphere(EntityUid grid, IEnumerable<Vector2i> positions, GasMixture air)
    {
        var atmosphere = EnsureComp<GridAtmosphereComponent>(grid);
        foreach (var position in positions)
        {
            var tile = GetOrNewTile(grid, atmosphere, position, invalidateNew: false);
            tile.Air = air.Clone();
            tile.Space = false;
            tile.MapAtmosphere = false;
            tile.AirArchived = null;
            tile.ArchivedCycle = 0;
            atmosphere.InvalidatedCoords.Add(position);
            AddActiveTile(atmosphere, tile);
        }
    }
}
