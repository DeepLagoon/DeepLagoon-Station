using Content.Client._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using System.Numerics;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Helpers;
using Content.Shared.Shuttles.Components;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Client.Shuttles.UI;

public partial class ShuttleNavControl
{
    private readonly List<BlipData> _beaconBlips = new();
    private readonly DrawVertexUV2DColor[] _spaceFillVertices = new DrawVertexUV2DColor[SpaceRegionRadarMesh.VertexCount];

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
                EntManager.System<SpaceEnvironmentOverlaySystem>().RadarFills(mapUid!.Value, environment.Definition), _spaceFillVertices);
        var system = starSystem.StarSystem;
        SpaceNavigationDraw.Body(handle, Font, system.Star.Position, system.Star.Name,
            Star.NAV_PIXEL_SIZE * system.Star.Radius, system.Star.Color, worldToView, PixelSize, origin, UIScale, true);
        foreach (var planet in system.Planets)
            SpaceNavigationDraw.Body(handle, Font, planet.Position, planet.Name, Planet.NAV_PIXEL_SIZE * planet.Radius,
                new Color(0.5f, 0.8f, 1f), worldToView, PixelSize, origin, UIScale, true);
    }

    // code stolen from IFF beacon rendering code for grids
    private void DrawIFFBeacons(DrawingHandleScreen handle, Matrix3x2 worldToView, MapCoordinates mapPos, EntityUid? mapUid)
    {
        if (!ShowIFF || mapUid == null)
            return;

        _beaconBlips.Clear();

        var uiXCentre = (int)Width / 2;
        var uiYCentre = (int)Height / 2;
        var blipSize = RadarBlipSize * 0.7f;

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
            var uiPosition = Vector2.Transform(worldPos, worldToView) / UIScale;

            var uiXOffset = uiPosition.X - uiXCentre;
            var uiYOffset = uiPosition.Y - uiYCentre;
            var uiDistance = Math.Max(1, (int)Math.Sqrt(Math.Pow(uiXOffset, 2) + Math.Pow(uiYOffset, 2)));
            var uiX = uiXCentre * uiXOffset / uiDistance;
            var uiY = uiYCentre * uiYOffset / uiDistance;

            var isOutsideRadarCircle = uiDistance > Math.Abs(uiX) && uiDistance > Math.Abs(uiY);
            if (isOutsideRadarCircle)
            {
                uiPosition = new Vector2(
                    x: uiXCentre * uiXOffset / uiDistance * 0.95f + uiXCentre,
                    y: uiYCentre * uiYOffset / uiDistance * 0.95f + uiYCentre
                );
            }

            NfAddBlipToList(_beaconBlips, isOutsideRadarCircle, uiPosition, uiXCentre, uiYCentre, color);

            var distance = Vector2.Distance(worldPos, mapPos.Position);
            var displayedDistance = distance < 50f ? $"{distance:0.0}" : distance < 1000 ? $"{distance:0}" : $"{distance / 1000:0.0}k";

            var lines = Loc.GetString("shuttle-console-iff-label", ("name", label), ("distance", displayedDistance)).Split('\n');
            var mainLabel = lines[0];

            var labelOffset = new Vector2(blipSize, -handle.GetDimensions(Font, mainLabel, 0.9f).Y * 0.5f);
            handle.DrawString(Font, (uiPosition + labelOffset) * UIScale, mainLabel, UIScale * 0.9f, color);

            if (!ShowIFFDetailed)
                continue;

            var stackOffset = labelOffset.Y + handle.GetDimensions(Font, mainLabel, 0.9f).Y;

            if (lines.Length > 1)
            {
                handle.DrawString(Font, (uiPosition + new Vector2(labelOffset.X, stackOffset)) * UIScale, lines[1], UIScale * 0.7f, color);
                stackOffset += handle.GetDimensions(Font, lines[1], 0.7f).Y;
            }

            var coordsText = $"({worldPos.X:0.0}, {worldPos.Y:0.0})";
            handle.DrawString(Font, (uiPosition + new Vector2(labelOffset.X, stackOffset)) * UIScale, coordsText, UIScale * 0.7f, color);
        }

        NfDrawBlips(handle, _beaconBlips);
    }
}
