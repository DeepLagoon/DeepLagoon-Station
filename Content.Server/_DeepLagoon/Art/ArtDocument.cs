using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Shared._DeepLagoon.Art;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace Content.Server._DeepLagoon.Art;

/// <summary>Decode bounded raster layers and compose on the server. Client previews are never archived as truth.</summary>
public static class ArtDocument
{
    public sealed class Project
    {
        public int Version { get; set; } = 1;
        public string Title { get; set; } = "";
        public List<Layer> Layers { get; set; } = new();
    }
    public sealed class Layer
    {
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1;
        public string Png { get; set; } = "";
        public string Mask { get; set; } = "";
    }
    public sealed record Baked(string Json, string Preview, string Hash, string Title, bool HasInk);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static Baked Bake(string json)
    {
        if (json.Length > ArtLimits.MaxDocumentChars) throw new InvalidDataException("dl-art-too-large");
        // Reject duplicate properties rather than allowing a different interpretation by the browser.
        using (var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 })) CheckDuplicates(parsed.RootElement);
        var project = JsonSerializer.Deserialize<Project>(json, Options) ?? throw new InvalidDataException("dl-art-invalid");
        if (project.Version != 1 || project.Title == null || project.Title.Length > 80 || project.Title.Any(char.IsControl)
            || project.Layers == null || project.Layers.Count is < 1 or > ArtLimits.MaxLayers)
            throw new InvalidDataException("dl-art-invalid");
        using var composite = new Image<Rgba32>(ArtLimits.Resolution, ArtLimits.Resolution);
        foreach (var layer in project.Layers)
        {
            if (layer == null || layer.Name == null || layer.Name.Length > 40 || layer.Name.Any(char.IsControl)
                || !float.IsFinite(layer.Opacity) || layer.Opacity is < 0 or > 1 || layer.Png == null || layer.Mask == null)
                throw new InvalidDataException("dl-art-invalid");
            using var ink = Decode(layer.Png, ArtLimits.Resolution);
            using var mask = Decode(layer.Mask, ArtLimits.Resolution);
            // Normalize every layer, even hidden ones: unsafe images cannot be parked in the document.
            layer.Png = ink == null ? "" : Encode(ink);
            layer.Mask = mask == null ? "" : Encode(mask);
            if (!layer.Visible || ink == null) continue;
            for (var y = 0; y < ink.Height; y++) for (var x = 0; x < ink.Width; x++)
            {
                var source = ink[x, y];
                var alpha = source.A / 255f * layer.Opacity * (mask == null ? 1 : mask[x, y].R / 255f);
                if (alpha <= 0) continue;
                var dest = composite[x, y]; var da = dest.A / 255f; var a = alpha + da * (1 - alpha);
                composite[x, y] = new Rgba32((byte)((source.R * alpha + dest.R * da * (1 - alpha)) / a),
                    (byte)((source.G * alpha + dest.G * da * (1 - alpha)) / a),
                    (byte)((source.B * alpha + dest.B * da * (1 - alpha)) / a), (byte)(a * 255));
            }
        }
        var hasInk = false;
        for (var y = 0; y < composite.Height && !hasInk; y++) for (var x = 0; x < composite.Width; x++)
            if (composite[x, y].A > 0) { hasInk = true; break; }
        var canonical = JsonSerializer.Serialize(project, Options);
        if (canonical.Length > ArtLimits.MaxDocumentChars) throw new InvalidDataException("dl-art-too-large");
        // A white paper backing affects presentation, not the transparent editing layers.
        using var paper = new Image<Rgba32>(ArtLimits.Resolution, ArtLimits.Resolution, new Rgba32(247, 240, 221));
        paper.Mutate(c => c.DrawImage(composite, 1f).Resize(ArtLimits.ThumbnailSize, ArtLimits.ThumbnailSize));
        return new Baked(canonical, Encode(paper), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), project.Title, hasInk);
    }

    public static string NormalizePreview(string png)
    {
        if (png.Length > ArtLimits.MaxPreviewChars) throw new InvalidDataException("dl-art-too-large");
        using var image = Decode(png, ArtLimits.ThumbnailSize) ?? throw new InvalidDataException("dl-art-invalid");
        return Encode(image);
    }
    private static Image<Rgba32>? Decode(string png, int size)
    {
        if (png.Length == 0) return null;
        if (png.Length > 4 * 1024 * 1024) throw new InvalidDataException("dl-art-too-large");
        var bytes = Convert.FromBase64String(png);
        var info = Image.Identify(bytes);
        // Static PNG Identify() has no per-animation-frame metadata (count zero).
        if (info.Width != size || info.Height != size || info.FrameMetadataCollection.Count > 1)
            throw new InvalidDataException("dl-art-invalid");
        var image = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 2, SkipMetadata = true }, bytes);
        if (image.Frames.Count == 1) return image;
        image.Dispose(); throw new InvalidDataException("dl-art-invalid");
    }
    private static string Encode(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, SkipMetadata = true });
        return Convert.ToBase64String(stream.ToArray());
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("dl-art-invalid");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) CheckDuplicates(value);
    }
}
