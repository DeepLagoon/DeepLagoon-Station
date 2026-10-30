using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._FarHorizons.StarSystem.Helpers;
using Robust.Client.Graphics;

namespace Content.Client._DeepLagoon.SpaceEnvironment;

public static class SpaceNavigationDraw
{
    public static void Body(DrawingHandleScreen handle, Font font, Vector2 world, string name, float radius,
        Color color, Matrix3x2 transform, Vector2 size, Vector2 origin, float uiScale, bool edge, ShaderInstance? portrait = null, bool planet = false)
    {
        var position = Vector2.Transform(world, transform);
        var margin = MathF.Min(20f * uiScale, MathF.Min(size.X, size.Y) * 0.2f);
        var center = size / 2f;
        var limit = center - new Vector2(margin);
        if (limit.X <= 0 || limit.Y <= 0) return;
        var delta = position - center;
        var outside = MathF.Abs(delta.X) > limit.X || MathF.Abs(delta.Y) > limit.Y;
        if (outside)
        {
            if (!edge) return;
            var factor = MathF.Max(MathF.Abs(delta.X) / limit.X, MathF.Abs(delta.Y) / limit.Y);
            position = center + delta / factor;
        }
        var scale = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var displayRadius = outside ? 3f * uiScale : DisplayRadius(radius * scale, uiScale, planet);
        if (!outside && portrait != null)
        {
            var previous = handle.GetShader();
            handle.UseShader(portrait);
            handle.DrawTextureRect(Texture.White, UIBox2.FromDimensions(position - new Vector2(displayRadius), new Vector2(displayRadius * 2)));
            handle.UseShader(previous);
        }
        else
        {
            handle.DrawCircle(position, displayRadius, color.WithAlpha(0.2f));
            handle.DrawCircle(position, displayRadius, color.WithAlpha(0.9f), false);
        }
        if (outside)
        {
            var direction = Vector2.Normalize(delta);
            handle.DrawLine(position, position - direction * (9f * uiScale), color);
        }
        var distance = Vector2.Distance(world, origin);
        var label = $"{name}  {(distance < 1000 ? $"{distance:0}" : $"{distance / 1000:0.0}k")}";
        var dimensions = handle.GetDimensions(font, label, uiScale * 0.8f);
        var labelPos = position + new Vector2(displayRadius + 4f, -dimensions.Y / 2f);
        labelPos.X = Math.Clamp(labelPos.X, 2f, MathF.Max(2f, size.X - dimensions.X - 2f));
        labelPos.Y = Math.Clamp(labelPos.Y, dimensions.Y, MathF.Max(dimensions.Y, size.Y - dimensions.Y));
        handle.DrawString(font, labelPos, label, uiScale * 0.8f, color);
    }

    public static float DisplayRadius(float scaledRadius, float uiScale, bool planet)
        => Math.Clamp(scaledRadius, (planet ? 16f : 4f) * uiScale, 70f * uiScale);

    public static void Regions(DrawingHandleScreen handle, Font font, SpaceEnvironmentComponent environment,
        Matrix3x2 transform, Vector2 size, float uiScale, Vector2[][] contours, DrawVertexUV2DColor[][] fills,
        DrawVertexUV2DColor[] vertices)
    {
        var scale = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var modulation = Color.FromSrgb(handle.Modulate);
        // All fills precede all labels and outlines, including overlapping regions.
        for (var regionIndex = 0; regionIndex < environment.Definition.Regions.Count; regionIndex++)
        {
            var region = environment.Definition.Regions[regionIndex];
            var center = Vector2.Transform(new Vector2(region.X, region.Y), transform);
            var radius = region.Radius * scale;
            if (radius < 2f || center.X + radius < 0 || center.Y + radius < 0
                || center.X - radius > size.X || center.Y - radius > size.Y) continue;
            var fill = fills[regionIndex];
            for (var i = 0; i < fill.Length; i++)
            {
                vertices[i] = fill[i];
                vertices[i].Position = Vector2.Transform(fill[i].Position, transform);
                vertices[i].Color *= modulation;
            }
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, vertices);
        }
        for (var regionIndex = 0; regionIndex < environment.Definition.Regions.Count; regionIndex++)
        {
            var region = environment.Definition.Regions[regionIndex];
            var points = contours[regionIndex];
            var center = Vector2.Transform(new Vector2(region.X, region.Y), transform);
            var radius = region.Radius * scale;
            if (center.X + radius < 0 || center.Y + radius < 0 || center.X - radius > size.X || center.Y - radius > size.Y) continue;
            var color = SpaceRegionGeometry.Color(region.Kind);
            var previous = Vector2.Transform(points[^1], transform);
            for (var i = 0; i < SpaceRegionGeometry.ContourSegments; i++)
            {
                var point = Vector2.Transform(points[i], transform);
                handle.DrawLine(previous, point, color.WithAlpha(0.7f));
                previous = point;
            }
            handle.DrawCircle(center, 3f * uiScale, color);
            if (radius < 18f || center.X < 0 || center.Y < 0 || center.X > size.X || center.Y > size.Y) continue;
            var text = Loc.GetString("space-environment-kind-" + region.Kind.ToString().ToLowerInvariant());
            if (environment.Surveyed.Contains(region.Id)) text += " " + Loc.GetString("space-environment-charted");
            handle.DrawString(font, center + new Vector2(6f, 0), text, uiScale * 0.8f, color);
        }
    }
}
