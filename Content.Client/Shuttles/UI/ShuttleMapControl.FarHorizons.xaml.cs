using System.Numerics;
using Content.Client._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Helpers;
using Robust.Client.Graphics;

namespace Content.Client.Shuttles.UI;

public sealed partial class ShuttleMapControl
{
    private TimeSpan _spaceRouteNext;
    private SpaceRouteEvaluator.Estimate _spaceRoute;
    private readonly DrawVertexUV2DColor[] _spaceFillVertices = new DrawVertexUV2DColor[SpaceRegionRadarMesh.VertexCount];

    private void DrawStarSystem(DrawingHandleScreen handle, Matrix3x2 matty)
    {
        if (!EntManager.TryGetComponent<StarSystemMapComponent>(_mapManager.GetMap(ViewingMap), out var starSystem)
            || starSystem.StarSystem == null) return;
        var worldToView = matty * Matrix3x2.CreateScale(new Vector2(MinimapScale, -MinimapScale))
            * Matrix3x2.CreateTranslation(MidPointVector);
        var map = _mapManager.GetMap(ViewingMap);
        if (EntManager.TryGetComponent<SpaceEnvironmentComponent>(map, out var environment))
        {
            SpaceNavigationDraw.Regions(handle, Font, environment, worldToView, PixelSize, UIScale,
                EntManager.System<SpaceEnvironmentOverlaySystem>().Contours(map, environment.Definition),
                EntManager.System<SpaceEnvironmentOverlaySystem>().RadarFills(map, environment.Definition), _spaceFillVertices);
            if (_shuttleEntity is { } ship && EntManager.TryGetComponent<TransformComponent>(ship, out var shipTransform)
                && shipTransform.MapUid == map && PixelSizeBox.Contains(GetLocalPosition(_inputs.MouseScreenPosition).Floored()))
            {
                var start = _xformSystem.GetWorldPosition(shipTransform);
                Matrix3x2.Invert(worldToView, out var inverse);
                var mouse = GetLocalPosition(_inputs.MouseScreenPosition);
                var end = Vector2.Transform(mouse, inverse);
                if (_timing.CurTime >= _spaceRouteNext)
                {
                    _spaceRouteNext = _timing.CurTime + TimeSpan.FromSeconds(0.2);
                    _spaceRoute = SpaceRouteEvaluator.Evaluate(start, end, environment.Definition.Regions);
                }
                handle.DrawLine(Vector2.Transform(start, worldToView), mouse, Color.Cyan.WithAlpha(0.4f));
                var text = FtlMode
                    ? Loc.GetString(_spaceRoute.PlasmaAtDeparture || _spaceRoute.PlasmaAtArrival
                        ? "space-environment-route-ftl-blocked" : "space-environment-route-ftl-clear")
                    : Loc.GetString("space-environment-route", ("dust", (int) _spaceRoute.DustDistance),
                        ("radiation", (int) _spaceRoute.RadiationDistance));
                handle.DrawString(Font, new Vector2(8, PixelHeight - 30), text, UIScale * 0.8f, Color.Cyan);
            }
        }
        var system = starSystem.StarSystem;
        SpaceNavigationDraw.Body(handle, Font, system.Star.Position, system.Star.Name,
            Star.MAP_PIXEL_SIZE * system.Star.Radius, system.Star.Color, worldToView, PixelSize, Offset, UIScale, false);
        foreach (var planet in system.Planets)
            SpaceNavigationDraw.Body(handle, Font, planet.Position, planet.Name, Planet.MAP_PIXEL_SIZE * planet.Radius,
                new Color(0.5f, 0.8f, 1f), worldToView, PixelSize, Offset, UIScale, false,
                EntManager.System<SpaceEnvironmentOverlaySystem>().PlanetShader(map, system, planet), planet: true);
    }
}
