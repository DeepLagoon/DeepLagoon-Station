using Content.Client._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using System.Numerics;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Helpers;
using Content.Shared.Shuttles.Components;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Client.Shuttles.UI;

public partial class ShuttleNavControl
{
    private readonly DrawVertexUV2DColor[] _spaceFillVertices = new DrawVertexUV2DColor[SpaceRegionRadarMesh.VertexCount];
    private readonly IGameTiming _spaceWeatherTiming = IoCManager.Resolve<IGameTiming>();

    private void DrawStarSystem(DrawingHandleScreen handle, Matrix3x2 worldToShuttle, Matrix3x2 shuttleToView, EntityUid? mapUid)
    {
        if (!EntManager.TryGetComponent<StarSystemMapComponent>(mapUid, out var starSystem) ||
            starSystem.StarSystem == null)
            return;

        var worldToView = worldToShuttle * shuttleToView;
        Matrix3x2.Invert(worldToView, out var inverse);
        var origin = Vector2.Transform(MidPointVector, inverse);
        if (EntManager.TryGetComponent<SpaceEnvironmentComponent>(mapUid, out var environment))
            SpaceNavigationDraw.Regions(handle, Font, environment, worldToView, PixelSize, UIScale,
                EntManager.System<SpaceEnvironmentOverlaySystem>().Contours(mapUid!.Value, environment.Definition),
                EntManager.System<SpaceEnvironmentOverlaySystem>().RadarFills(mapUid!.Value, environment.Definition), _spaceFillVertices,
                _spaceWeatherTiming.CurTime.TotalSeconds);
        var system = starSystem.StarSystem;
        if (starSystem.Definition?.NavigationOnly != true)
            SpaceNavigationDraw.Body(handle, Font, system.Star.Position, system.Star.Name,
            Star.NAV_PIXEL_SIZE * system.Star.Radius, system.Star.Color, worldToView, PixelSize, origin, UIScale, true);
        foreach (var planet in system.Planets)
            SpaceNavigationDraw.Body(handle, Font, planet.Position, planet.Name, Planet.NAV_PIXEL_SIZE * planet.Radius,
                new Color(0.5f, 0.8f, 1f), worldToView, PixelSize, origin, UIScale, true,
                EntManager.System<SpaceEnvironmentOverlaySystem>().PlanetShader(mapUid!.Value, system, planet), planet: true);
    }

    // code stolen from IFF beacon rendering code for grids
    private void DrawIFFBeacons(DrawingHandleScreen handle, Matrix3x2 worldToView, MapCoordinates mapPos, EntityUid? mapUid)
    {
        if (!ShowIFF || mapUid == null)
            return;

        var query = EntManager.AllEntityQueryEnumerator<IFFComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var iff, out var xform))
        {
            if (xform.MapUid != mapUid ||
                EntManager.HasComponent<MapGridComponent>(uid) ||
                EntManager.HasComponent<SectorLandmarkComponent>(uid) ||
                (iff.Flags & IFFFlags.Hide) != 0x0)
                continue;

            if (_shuttles.GetIFFLabel(uid, component: iff) is not { } label)
                continue;

            var color = _shuttles.GetIFFColor(uid, self: false, iff);
            var worldPos = _transform.GetWorldPosition(uid);
            var screenPosition = Vector2.Transform(worldPos, worldToView);
            CollectSignature(handle, uid, screenPosition, worldPos, Vector2.Distance(worldPos, mapPos.Position),
                label, color, hidden: false, iff, beacon: true);
        }
    }
}
