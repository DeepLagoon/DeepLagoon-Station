using System.Globalization;
using System.IO;
using System.Linq;
using Content.Client._DeepLagoon.Tattoos;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using Robust.Shared.ContentPack;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Humanoid;

/// <summary>
/// Generates ordinary directional RSI states so previews, click maps and other sprite consumers
/// see the same fitted pixels as the world renderer. The cache is keyed by art, independent of entities.
/// </summary>
internal sealed partial class ProstheticSpriteCache
{
    private static readonly ResPath TextureRoot = new("/Textures");
    private static readonly ResPath GeneratedRoot = new("_Generated/Prosthetics");
    private const string StateName = "fitted";

    [Dependency] private IResourceCache _resources = default!;
    private readonly Dictionary<(SpriteSpecifier.Rsi Prosthesis, SpriteSpecifier.Rsi Anatomy), SpriteSpecifier.Rsi> _cache = new();
    private MemoryContentRoot? _generated;

    public SpriteSpecifier.Rsi Fit(SpriteSpecifier.Rsi prosthesis, SpriteSpecifier.Rsi anatomy)
    {
        var key = (prosthesis, anatomy);
        if (_cache.TryGetValue(key, out var fitted))
            return fitted;

        if (!_resources.TryGetResource<RSIResource>(TextureRoot / prosthesis.RsiPath, out var sourceRsi) ||
            !sourceRsi.RSI.TryGetState(prosthesis.RsiState, out var source) ||
            !_resources.TryGetResource<RSIResource>(TextureRoot / anatomy.RsiPath, out var targetRsi) ||
            !targetRsi.RSI.TryGetState(anatomy.RsiState, out var target))
            return prosthesis;

        var size = target.Size;
        var directions = Math.Max(source.Icons.Length, target.Icons.Length);
        using var sheet = new Image<Rgba32>(size.X * source.DelayCount, size.Y * directions);
        for (var direction = 0; direction < directions; direction++)
        {
            var targetDirection = FrameDirection(target, direction);
            using var mask = TattooSpriteFrames.Read(target, targetDirection);
            var sourceDirection = FrameDirection(source, direction);
            for (var frame = 0; frame < source.DelayCount; frame++)
            {
                using var art = TattooSpriteFrames.Read(source, sourceDirection, frame);
                // Human-layout art can omit a far limb that the recipient's silhouette shows.
                // In that case, take the prosthesis colors from its front view.
                var rows = ReadRows(art);
                if (rows.Count == 0 && sourceDirection != 0)
                {
                    using var front = TattooSpriteFrames.Read(source, 0, frame);
                    rows = ReadRows(front);
                }

                FitFrame(rows, mask, sheet, frame * size.X, direction * size.Y);
            }
        }

        if (_generated == null)
        {
            _generated = new MemoryContentRoot();
            _resources.AddRoot(TextureRoot / GeneratedRoot, _generated);
        }

        var path = new ResPath($"{_cache.Count}.rsi");
        var delays = string.Join(",", source.Delays.Select(delay => delay.ToString("R", CultureInfo.InvariantCulture)));
        var directionalDelays = string.Join(",", Enumerable.Repeat($"[{delays}]", directions));
        var manifest = $$"""
            {"version":1,"size":{"x":{{size.X}},"y":{{size.Y}}},
             "states":[{"name":"{{StateName}}","directions":{{directions}},"delays":[{{directionalDelays}}]}]}
            """;
        using var png = new MemoryStream();
        sheet.SaveAsPng(png);
        _generated.AddOrUpdateFile(path / "meta.json", manifest);
        _generated.AddOrUpdateFile(path / (StateName + ".png"), png.ToArray());

        fitted = new SpriteSpecifier.Rsi(GeneratedRoot / path, StateName);
        _cache.Add(key, fitted);
        return fitted;
    }

    private static int FrameDirection(RSI.State state, int direction) => state.RsiDirections switch
    {
        RsiDirectionType.Dir1 => 0,
        RsiDirectionType.Dir4 => (int) ((RsiDirection) direction).RoundToCardinal(),
        _ => direction,
    };

    private static List<List<Rgba32>> ReadRows(Image<Rgba32> art)
    {
        var rows = new List<List<Rgba32>>();
        var pixels = art.GetPixelSpan();
        for (var y = 0; y < art.Height; y++)
        {
            var row = new List<Rgba32>();
            for (var x = 0; x < art.Width; x++)
            {
                var color = pixels[y * art.Width + x];
                if (color.A != 0)
                    row.Add(color);
            }

            if (row.Count != 0)
                rows.Add(row);
        }

        return rows;
    }

    private static void FitFrame(List<List<Rgba32>> art, Image<Rgba32> mask, Image<Rgba32> sheet, int left, int top)
    {
        if (art.Count == 0)
            return;

        var maskPixels = mask.GetPixelSpan();
        var sheetPixels = sheet.GetPixelSpan();
        var rows = new List<(int Y, List<int> Columns)>();
        for (var y = 0; y < mask.Height; y++)
        {
            var columns = new List<int>();
            for (var x = 0; x < mask.Width; x++)
            {
                if (maskPixels[y * mask.Width + x].A != 0)
                    columns.Add(x);
            }

            if (columns.Count != 0)
                rows.Add((y, columns));
        }

        // Sample the existing metal colors along the length and width of each limb. The native
        // silhouette stays in place, including the rows that meet the torso, hand or foot.
        for (var row = 0; row < rows.Count; row++)
        {
            var (y, columns) = rows[row];
            var colors = art[Sample(row, rows.Count, art.Count)];
            for (var column = 0; column < columns.Count; column++)
            {
                var x = columns[column];
                var color = colors[Sample(column, columns.Count, colors.Count)];
                color.A = maskPixels[y * mask.Width + x].A;
                sheetPixels[(top + y) * sheet.Width + left + x] = color;
            }
        }
    }

    private static int Sample(int index, int outputSize, int inputSize) =>
        (2 * index + 1) * inputSize / (2 * outputSize);
}
