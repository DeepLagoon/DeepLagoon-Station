using System.Numerics;
using Robust.Client.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.BodyCustomization;

/// <summary>
/// Builds an inverse, nearest-pixel cloth warp. Coordinates refer to the original clothing frame,
/// including the species displacement. RG store integer source pixels, B stores cloth lighting
/// (128 is neutral), A stores displacement opacity.
/// Keeping the fabric's alpha (rather than painting the body's silhouette) preserves cut-outs and hems.
/// </summary>
public static class ClothingBodyFitMap
{
    public readonly record struct Anatomy(Image<Rgba32> Shape, Image<Rgba32> Anchor, bool StretchVertically = true);

    public readonly record struct Contour(Image<Rgba32> Shape, bool BelowWaist = false);

    public static Image<Rgba32>? Build(Image<Rgba32> body,
        IReadOnlyList<Anatomy> anatomy,
        Image<Rgba32> garment, Image<Rgba32>? displacement, int padding, bool force = false,
        Image<Rgba32>? hands = null, IReadOnlyList<Contour>? contours = null)
    {
        var width = body.Width;
        var height = body.Height;
        var coverage = new byte[width * height];
        var source = new Rgba32[width * height];
        var garmentPixels = garment.GetPixelSpan();
        var displacementPixels = displacement != null ? displacement.GetPixelSpan() : default;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var d = displacement == null ? new Rgba32(128, 128, 0, 255) :
                displacementPixels[y * displacement.Height / height * displacement.Width + x * displacement.Width / width];
            var sx = x + d.R - 128;
            var sy = y + d.G - 128;
            if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
            source[y * width + x] = new Rgba32((byte) sx, (byte) sy, 128, d.A);
            coverage[y * width + x] = (byte) (garmentPixels[sy * width + sx].A * d.A / 255);
        }

        var original = Bounds(body);
        var bodyPixels = body.GetPixelSpan();
        var handPixels = hands != null ? hands.GetPixelSpan() : default;
        using var target = body.Clone();
        var targetPixels = target.GetPixelSpan();
        var rowPreserving = new bool[width * height];
        var contour = new Vector3[width * height];
        var hasContour = false;
        var patches = new List<(Vector2[] Rows, int Top, int Bottom, int AnchorTop, int AnchorBottom)>();
        foreach (var (shape, anchor, stretchVertically) in anatomy)
        {
            var anchorPixels = anchor.GetPixelSpan();
            var shapePixels = shape.GetPixelSpan();
            var anchorRows = Bounds(anchor);
            var covered = 0;
            var total = 0;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (anchorPixels[y * width + x].A < 128) continue;
                total++;
                if (coverage[y * width + x] >= 128) covered++;
            }
            // Side sprites often leave a hand cut-out inside the attachment. Test the fabric at the
            // body's attaching edge as well; an interior cut-out must not discard the entire part.
            var edgeTotal = 0;
            var edgeCovered = 0;
            for (var y = 0; y < height; y++)
            {
                var row = original[y];
                var root = anchorRows[y];
                if (row.Y < row.X || root.Y < root.X) continue;
                var center = (row.X + row.Y) / 2;
                if (root.X <= center)
                { edgeTotal++; if (coverage[y * width + (int) row.X] >= 128) edgeCovered++; }
                if (root.Y >= center)
                { edgeTotal++; if (coverage[y * width + (int) row.Y] >= 128) edgeCovered++; }
            }
            // Use only the undeformed attachment, never a large part overlapping unrelated pants.
            if (total == 0 || covered * 2 < total && (edgeTotal == 0 || edgeCovered * 2 < edgeTotal)) continue;
            if (contours != null)
            {
                foreach (var detail in contours)
                {
                    if (!ReferenceEquals(detail.Shape, shape)) continue;
                    // The upper third of the small rear attachment joins the lower back. Larger
                    // shapes can reach farther up, but must not imprint their volume on the shirt.
                    var start = detail.BelowWaist
                        ? FirstRow(anchorRows) + (LastRow(anchorRows) + 1 - FirstRow(anchorRows)) / 3
                        : 0;
                    AddContour(shape, contour, start);
                    hasContour = true;
                }
            }
            var shapeRows = Bounds(shape);
            var shapeBottom = LastRow(shapeRows);
            var anchorBottom = LastRow(anchorRows);
            if (shapeBottom >= 0 && anchorBottom >= 0)
            {
                var shapeTop = FirstRow(shapeRows);
                var anchorTop = FirstRow(anchorRows);
                // Cloth over a hanging part must come from its attachment, not from the bare crotch or
                // trouser leg further down the image. The inverse patch stretches the same fabric vertically.
                if (stretchVertically && shapeBottom - shapeTop > anchorBottom - anchorTop + 2)
                    patches.Add((shapeRows, shapeTop, shapeBottom, anchorTop, anchorBottom));
            }
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (shapePixels[y * width + x].A < 128) continue;
                targetPixels[y * width + x] = new Rgba32(255, 255, 255, 255);
                rowPreserving[y * width + x] |= !stretchVertically;
            }
        }

        var expanded = Bounds(target);
        var bodyBottom = LastRow(original);
        var targetBottom = LastRow(expanded);
        if (bodyBottom < 0)
        {
            if (!force) return null;
            var identity = new Image<Rgba32>(width + padding * 2, height + padding * 2);
            var pixels = identity.GetPixelSpan();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) pixels[(y + padding) * identity.Width + x + padding] = source[y * width + x];
            return identity;
        }
        var top = 0;
        while (original[top].Y < original[top].X) top++;
        var anchorY = (top + bodyBottom) / 2f;
        var left = new float[height];
        var right = new float[height];
        var changed = targetBottom > bodyBottom || patches.Count > 0 || hasContour;
        for (var y = top; y <= targetBottom; y++)
        {
            var sy = SourceY(y + .5f);
            var row = Math.Clamp((int) sy, top, bodyBottom);
            if (original[row].Y < original[row].X || expanded[y].Y < expanded[y].X) continue;
            left[y] = Math.Max(0, original[row].X - expanded[y].X);
            right[y] = Math.Max(0, expanded[y].Y - original[row].Y);
            changed |= left[y] > 0 || right[y] > 0;
        }
        if (!changed && !force) return null;

        // A short falloff connects fabric across pixel steps without narrowing the required silhouette.
        var smoothLeft = Smooth(left);
        var smoothRight = Smooth(right);
        var result = new Image<Rgba32>(width + padding * 2, height + padding * 2);
        var resultPixels = result.GetPixelSpan();
        for (var y = 0; y < result.Height; y++)
        for (var x = 0; x < result.Width; x++)
        {
            var py = y - padding + .5f;
            var sy = SourceY(py);
            var px = x - padding + .5f;
            var ty = Math.Clamp(y - padding, 0, height - 1);
            var preserveRow = x >= padding && x < width + padding && y >= padding && y < height + padding &&
                              rowPreserving[ty * width + x - padding];
            foreach (var patch in patches)
            {
                if (py < patch.Top || py >= patch.Bottom + 1) continue;
                var edge = patch.Rows[ty];
                if (edge.Y < edge.X) continue;
                var distance = Math.Max(edge.X - px, px - edge.Y - 1);
                var weight = Math.Clamp(1 - distance / 2, 0, 1);
                var fittedY = patch.AnchorTop + (py - patch.Top) *
                    (patch.AnchorBottom + 1f - patch.AnchorTop) / (patch.Bottom + 1f - patch.Top);
                sy += (fittedY - sy) * weight;
            }
            var row = (int) MathF.Floor(sy);
            if (row < 0 || row >= height) continue;
            var bounds = original[row];
            if (bounds.Y >= bounds.X)
            {
                var center = (bounds.X + bounds.Y + 1) / 2;
                var growLeft = Math.Max(smoothLeft[ty], bounds.X - expanded[ty].X);
                var growRight = Math.Max(smoothRight[ty], expanded[ty].Y - bounds.Y);
                var l = bounds.X - growLeft;
                var r = bounds.Y + 1 + growRight;
                if (px < l || px >= r)
                {
                    // Sleeves and trim outside the new torso stay at their original coordinates.
                    // Translating these pixels by the torso growth adds the sleeve width a second time.
                    row = y - padding;
                }
                else if (px < center)
                {
                    var start = (int) bounds.X;
                    while (start < center && coverage[row * width + start] < 128) start++;
                    var end = start;
                    while (end < center && coverage[row * width + end] >= 128) end++;
                    var pivot = Math.Min(center, end);
                    var destinationPivot = Math.Max(pivot, original[ty].X);
                    // Stretch only the contiguous fabric on this side. Keep hand holes and open
                    // jacket fronts anchored instead of carrying them into the expanded silhouette.
                    // Some trousers are narrower than the skin row (notably digitigrade legs).
                    // Rear volume must reach the fabric edge even when it stays inside that skin row.
                    if ((growLeft > 0 || preserveRow) && start < pivot && px < destinationPivot && l < start)
                        px = start + (px - l) * (pivot - start) / (destinationPivot - l);
                }
                else
                {
                    var end = (int) bounds.Y;
                    while (end + 1 > center && coverage[row * width + end] < 128) end--;
                    var start = end;
                    while (start + 1 > center && coverage[row * width + start] >= 128) start--;
                    var pivot = Math.Max(center, start + 1);
                    var destinationPivot = Math.Min(pivot, original[ty].Y + 1);
                    if ((growRight > 0 || preserveRow) && end + 1 > pivot && px >= destinationPivot && r > end + 1)
                        px = pivot + (px - destinationPivot) * (end + 1 - pivot) / (r - destinationPivot);
                }
            }
            var column = (int) MathF.Floor(px);
            if (row >= 0 && row < height && column >= 0 && column < width)
            {
                var dx = x - padding;
                // Hands (and gloves drawn over them) do not move with hanging anatomy. Do not drag
                // their openings down into previously covered fabric. Restore that fabric from the
                // original destination texel; unrelated garment cut-outs still follow the warp.
                if (hands != null && dx >= 0 && dx < width && y >= padding && y < height + padding &&
                    coverage[row * width + column] < 128 && handPixels[row * width + column].A >= 128 &&
                    handPixels[ty * width + dx].A < 128 && coverage[ty * width + dx] >= 128)
                {
                    row = ty;
                    column = dx;
                }
                // The gap between native legs is not a cut-out in the fabric. A hanging part can
                // cover it: sample the adjacent trouser fabric instead of stretching the leg gap.
                // Actual garment cut-outs over opaque skin remain transparent.
                if (dx >= 0 && dx < width && y >= padding && y < height + padding &&
                    targetPixels[ty * width + dx].A >= 128 && bodyPixels[ty * width + dx].A < 128 &&
                    coverage[row * width + column] < 128 && bodyPixels[row * width + column].A < 128 &&
                    column > original[row].X && column < original[row].Y)
                {
                    for (var distance = 1; distance < width; distance++)
                    {
                        var a = column - distance;
                        var b = column + distance;
                        if (a >= original[row].X && coverage[row * width + a] >= 128)
                        { column = a; break; }
                        if (b <= original[row].Y && coverage[row * width + b] >= 128)
                        { column = b; break; }
                    }
                }
                var sample = source[row * width + column];
                if (hasContour && dx >= 0 && dx < width && y >= padding && y < height + padding &&
                    coverage[row * width + column] >= 128 && (hands == null || handPixels[ty * width + dx].A < 128))
                {
                    var surface = contour[ty * width + dx];
                    var cx = column + (int) MathF.Round(surface.X);
                    var cy = row + (int) MathF.Round(surface.Y);
                    // Bending a fabric pattern must not move a cut-out into covered skin or pull
                    // a fixed hand opening along with it. All garment layers share this map.
                    if (cx >= 0 && cx < width && cy >= 0 && cy < height &&
                        coverage[cy * width + cx] == coverage[row * width + column] &&
                        (hands == null || handPixels[cy * width + cx].A < 128))
                        sample = source[cy * width + cx];
                    sample.B = (byte) Math.Clamp((int) MathF.Round((1 + surface.Z) * 128), 0, 255);
                }
                resultPixels[y * result.Width + x] = sample;
            }
        }
        return result;

        float SourceY(float y) => targetBottom > bodyBottom && y > anchorY
            ? anchorY + (y - anchorY) * (bodyBottom + 1 - anchorY) / (targetBottom + 1 - anchorY)
            : y;
    }

    /// <summary>
    /// A soft volume hint derived only from the part's silhouette. Never transfer skin color or
    /// anatomical surface details onto clothing. A blurred distance field follows the lobes of the
    /// selected shape, bends its fabric pattern by at most one texel, and lights it from above.
    /// </summary>
    private static void AddContour(Image<Rgba32> shape, Vector3[] output, int firstRow)
    {
        var width = shape.Width;
        var height = shape.Height;
        var pixels = shape.GetPixelSpan();
        var left = width;
        var right = -1;
        var distance = new float[width * height];
        for (var i = 0; i < distance.Length; i++)
        {
            if (i / width < firstRow || pixels[i].A < 128) continue;
            distance[i] = width + height;
            left = Math.Min(left, i % width);
            right = Math.Max(right, i % width);
        }
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            if (distance[i] == 0) continue;
            distance[i] = Math.Min(distance[i], Math.Min(At(distance, x - 1, y), At(distance, x, y - 1)) + 1);
            distance[i] = Math.Min(distance[i], Math.Min(At(distance, x - 1, y - 1), At(distance, x + 1, y - 1)) + 1.414214f);
        }
        var maximum = 0f;
        for (var y = height - 1; y >= 0; y--)
        for (var x = width - 1; x >= 0; x--)
        {
            var i = y * width + x;
            if (distance[i] == 0) continue;
            distance[i] = Math.Min(distance[i], Math.Min(At(distance, x + 1, y), At(distance, x, y + 1)) + 1);
            distance[i] = Math.Min(distance[i], Math.Min(At(distance, x + 1, y + 1), At(distance, x - 1, y + 1)) + 1.414214f);
            maximum = Math.Max(maximum, distance[i]);
        }
        if (maximum == 0) return;
        var amount = Math.Min(1, maximum / 3);
        // These paired parts may have a completely joined silhouette (especially the rear view).
        // A broad, shallow valley hints at two volumes without printing anatomy details on fabric.
        var center = (left + right) / 2f;
        var valleyWidth = Math.Max(1, (right - left + 1) * .08f);
        for (var i = 0; i < distance.Length; i++)
        {
            var across = (i % width - center) / valleyWidth;
            distance[i] = MathF.Sqrt(distance[i] / maximum) * (1 - .32f * MathF.Exp(-across * across));
        }
        var surface = new float[distance.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            surface[y * width + x] += At(distance, x + dx, y + dy) * (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1) / 16;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            if (y < firstRow || pixels[i].A < 128) continue;
            var gx = (At(surface, x + 1, y) - At(surface, x - 1, y)) / 2;
            var gy = (At(surface, x, y + 1) - At(surface, x, y - 1)) / 2;
            var crease = Math.Max(0, (At(surface, x - 1, y) + At(surface, x + 1, y)) / 2 - surface[i]);
            var shade = Math.Clamp((gy * 1.1f - crease * .55f - (1 - surface[i]) * .08f) * amount, -.34f, .17f);
            output[i] = new Vector3(Math.Clamp(gx * 2.2f * amount, -1, 1), Math.Clamp(gy * 2.2f * amount, -1, 1), shade);
        }

        float At(float[] values, int x, int y) => x >= 0 && x < width && y >= 0 && y < height ? values[y * width + x] : 0;
    }

    private static Vector2[] Bounds(Image<Rgba32> image)
    {
        var rows = new Vector2[image.Height];
        var pixels = image.GetPixelSpan();
        for (var y = 0; y < image.Height; y++)
        {
            rows[y] = new Vector2(image.Width, -1);
            for (var x = 0; x < image.Width; x++)
            {
                if (pixels[y * image.Width + x].A < 128) continue;
                rows[y].X = Math.Min(rows[y].X, x);
                rows[y].Y = x;
            }
        }
        return rows;
    }

    private static int LastRow(Vector2[] rows)
    {
        for (var y = rows.Length - 1; y >= 0; y--)
            if (rows[y].Y >= rows[y].X) return y;
        return -1;
    }

    private static int FirstRow(Vector2[] rows)
    {
        for (var y = 0; y < rows.Length; y++)
            if (rows[y].Y >= rows[y].X) return y;
        return 0;
    }

    private static float[] Smooth(float[] input)
    {
        var output = new float[input.Length];
        for (var y = 0; y < input.Length; y++)
        for (var offset = -2; offset <= 2; offset++)
            if (y + offset >= 0 && y + offset < input.Length)
                output[y] = Math.Max(output[y], input[y + offset] - Math.Abs(offset));
        return output;
    }
}
