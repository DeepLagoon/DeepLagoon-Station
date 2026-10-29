using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Robust.Client.Graphics;

namespace Content.Client._DeepLagoon.SpaceEnvironment;

/// <summary>Cached, softly shaded radar fill, confined to the same polygon as the navigation boundary.</summary>
public static class SpaceRegionRadarMesh
{
    public const int VertexCount = SpaceRegionGeometry.ContourSegments * 9;

    public static DrawVertexUV2DColor[] Build(SpaceRegion region, Vector2[] contour)
    {
        var vertices = new DrawVertexUV2DColor[VertexCount];
        var center = new Vector2(region.X, region.Y);
        var color = SpaceRegionGeometry.Color(region.Kind);
        var density = 0.6f + 0.4f * Math.Clamp(region.Intensity, 0f, 1f);
        var random = new SectorRandom(SectorRandom.Derive(region.Seed, "radar-fill"));
        var middle = new DrawVertexUV2DColor[SpaceRegionGeometry.ContourSegments];
        for (var i = 0; i < middle.Length; i++)
        {
            var position = Vector2.Lerp(center, contour[i], random.Range(0.48f, 0.68f));
            middle[i] = new DrawVertexUV2DColor(position,
                Color.FromSrgb(color.WithAlpha(random.Range(0.07f, 0.14f) * density)));
        }

        var core = new DrawVertexUV2DColor(center, Color.FromSrgb(color.WithAlpha(0.16f * density)));
        var edgeColor = Color.FromSrgb(color.WithAlpha(0.018f * density));
        var index = 0;
        for (var i = 0; i < middle.Length; i++)
        {
            var next = (i + 1) % middle.Length;
            var outer = new DrawVertexUV2DColor(contour[i], edgeColor);
            var outerNext = new DrawVertexUV2DColor(contour[next], edgeColor);
            vertices[index++] = core;
            vertices[index++] = middle[i];
            vertices[index++] = middle[next];
            vertices[index++] = middle[i];
            vertices[index++] = outer;
            vertices[index++] = outerNext;
            vertices[index++] = middle[i];
            vertices[index++] = outerNext;
            vertices[index++] = middle[next];
        }
        return vertices;
    }
}
