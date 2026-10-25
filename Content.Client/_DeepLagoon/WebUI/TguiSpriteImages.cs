using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.Loadouts;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Read first sprite frames from packaged resources, without GPU readbacks.</summary>
public sealed partial class TguiSpriteImages
{
    public static TguiSpriteImages Shared => IoCManager.Resolve<TguiSpriteImages>();

    private readonly Dictionary<SpriteSpecifier, string> _frames = new();
    private readonly Dictionary<string, List<TguiData>> _items = new();
    private readonly Dictionary<(ResPath, byte), string> _tileFrames = new();
    private readonly Dictionary<ResPath, PackedSheet> _sheets = new();
    private sealed record PackedSheet(Image<Rgba32> Image, RSI Rsi, int Left, int Top);

    public void Reset()
    {
        _frames.Clear();
        _items.Clear();
        _tileFrames.Clear();
        _directionalFrames.Clear();
        ReleaseSheets();
    }

    /// <summary>Tile sprites can be variant strips. Select one 32×32 frame.</summary>
    public string TileFrame(ResPath path, byte variant = 0)
    {
        if (_tileFrames.TryGetValue((path, variant), out var cached)) return cached;
        try
        {
            using var input = IoCManager.Resolve<IResourceCache>().ContentFileRead(new ResPath("/Textures") / path);
            using var image = Image.Load<Rgba32>(input);
            var x = Math.Min(variant * 32, Math.Max(0, image.Width - 32));
            using var frame = image.Clone(context => context.Crop(new Rectangle(x, 0, Math.Min(32, image.Width), Math.Min(32, image.Height))));
            using var output = new MemoryStream(); frame.SaveAsPng(output);
            return _tileFrames[(path, variant)] = "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception) { return _tileFrames[(path, variant)] = ""; }
    }

    public string Frame(SpriteSpecifier spec)
    {
        if (_frames.TryGetValue(spec, out var cached)) return cached;
        var resources = IoCManager.Resolve<IResourceCache>();
        var path = spec switch
        {
            SpriteSpecifier.Rsi rsi => new ResPath("/Textures") / rsi.RsiPath / (rsi.RsiState + ".png"),
            SpriteSpecifier.Texture texture => new ResPath("/Textures") / texture.TexturePath,
            _ => new ResPath("/Textures/noSprite.png")
        };
        try
        {
            if (!resources.TryContentFileRead(path, out var stream))
            {
                if (spec is not SpriteSpecifier.Rsi packedRsi) return _frames[spec] = "";
                var sheet = Sheet(resources, new ResPath("/Textures") / packedRsi.RsiPath);
                return _frames[spec] = PackedFrame(sheet.Image, sheet.Rsi, packedRsi.RsiState, sheet.Left, sheet.Top);
            }
            if (stream == null) return _frames[spec] = "";
            using var input = stream;
            using var image = Image.Load<Rgba32>(stream);
            using var output = new MemoryStream();
            if (spec is SpriteSpecifier.Rsi rsi)
            {
                var size = resources.GetResource<RSIResource>(new ResPath("/Textures") / rsi.RsiPath).RSI.Size;
                using var frame = image.Clone(context => context.Crop(new Rectangle(0, 0, size.X, size.Y)));
                frame.SaveAsPng(output);
            }
            else image.SaveAsPng(output);
            return _frames[spec] = "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception) { return _frames[spec] = ""; }
    }

    private PackedSheet Sheet(IResourceCache resources, ResPath path)
    {
        if (_sheets.TryGetValue(path, out var sheet)) return sheet;
        var rsi = resources.GetResource<RSIResource>(path).RSI;
        var (left, top) = Origin(rsi);
        using var input = resources.ContentFileRead(path.WithExtension("rsic"));
        var image = Image.Load<Rgba32>(input);
        sheet = new PackedSheet(image, rsi, left, top);
        // Share decoded sheets within a batch, then release their pixel buffers.
        if (_sheets.Count == 0) Timer.Spawn(0, ReleaseSheets);
        _sheets.Add(path, sheet);
        return sheet;
    }

    private void ReleaseSheets()
    {
        foreach (var sheet in _sheets.Values) sheet.Image.Dispose();
        _sheets.Clear();
    }

    public static string PackedFrame(Image<Rgba32> image, RSI rsi, string stateName)
    {
        if (!rsi.TryGetState(stateName, out _))
            throw new InvalidDataException("RSIC state not found: " + stateName);
        var (left, top) = Origin(rsi);
        return PackedFrame(image, rsi, stateName, left, top);
    }

    private static (int Left, int Top) Origin(RSI rsi)
    {
        // The first south frame of the first state is at sheet index zero.
        // All states share a GPU atlas offset; its minimum X/Y is the RSI origin.
        // Subtract it to recover coordinates inside this RSI's packed PNG.
        var regions = rsi.Select(state => ((AtlasTexture) state.Frame0).SubRegion).ToArray();
        return ((int) regions.Min(r => r.Left), (int) regions.Min(r => r.Top));
    }

    private static string PackedFrame(Image<Rgba32> image, RSI rsi, string stateName, int left, int top)
    {
        if (!rsi.TryGetState(stateName, out var selected))
            throw new InvalidDataException("RSIC state not found: " + stateName);
        var region = ((AtlasTexture) selected.Frame0).SubRegion;
        var rectangle = new Rectangle((int) region.Left - left, (int) region.Top - top, rsi.Size.X, rsi.Size.Y);
        using var frame = image.Clone(context => context.Crop(rectangle));
        using var output = new MemoryStream();
        frame.SaveAsPng(output);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
    }

    public IEnumerable<TguiData> EntityImages(EntityUid entity)
    {
        var entities = IoCManager.Resolve<IEntityManager>();
        if (!entities.TryGetComponent<SpriteComponent>(entity, out var sprite)) yield break;
        Color? tint = entities.TryGetComponent<AppearanceComponent>(entity, out var appearance) &&
            entities.System<SharedAppearanceSystem>().TryGetData<Color>(entity, PersonalLoadoutVisuals.Color, out var color, appearance) ? color : null;
        foreach (var image in SpriteImages(sprite, tint)) yield return image;
    }

    private IEnumerable<TguiData> SpriteImages(SpriteComponent sprite, Color? tint = null)
    {
        foreach (var layer in sprite.AllLayers.Where(layer => layer.Visible && layer.ActualRsi != null && layer.RsiState.IsValid))
            yield return new TguiData().String("url", Frame(new SpriteSpecifier.Rsi(layer.ActualRsi!.Path, layer.RsiState.Name!)))
                .String("color", (layer.Color * (tint ?? sprite.Color)).ToHexNoAlpha());
    }

    // Mapping previews use the actual sprite origin and native pixel size, not a centered catalogue icon.
    public IEnumerable<TguiData> MapItem(string id)
    {
        var proto = IoCManager.Resolve<IPrototypeManager>().Index<EntityPrototype>(id);
        if (!proto.TryGetComponent<SpriteComponent>(out var sprite)) return Item(id);
        var result = MapSprite(sprite).ToList();
        return result.Count == 0 ? Item(id) : result;
    }

    public IEnumerable<TguiData> MapEntity(EntityUid entity) =>
        IoCManager.Resolve<IEntityManager>().TryGetComponent<SpriteComponent>(entity, out var sprite) ? MapSprite(sprite) : [];

    private IEnumerable<TguiData> MapSprite(SpriteComponent sprite)
    {
        var result = new List<TguiData>();
        foreach (var layer in sprite.AllLayers.Where(layer => layer.Visible && layer.ActualRsi != null && layer.RsiState.IsValid))
        {
            var matrix = System.Numerics.Matrix3x2.Multiply(((SpriteComponent.Layer) layer).LocalMatrix, sprite.LocalMatrix);
            var size = layer.PixelSize;
            result.Add(new TguiData().String("url", Frame(new SpriteSpecifier.Rsi(layer.ActualRsi!.Path, layer.RsiState.Name!)))
                .String("color", (layer.Color * sprite.Color).ToHexNoAlpha()).Bool("native", true)
                .Number("width", size.X).Number("height", size.Y)
                .Number("m11", matrix.M11).Number("m12", matrix.M12).Number("m21", matrix.M21).Number("m22", matrix.M22)
                .Number("m31", matrix.M31).Number("m32", matrix.M32));
        }
        return result;
    }

    public IEnumerable<TguiData> Item(string id)
    {
        if (_items.TryGetValue(id, out var cached)) return cached;
        var result = new List<TguiData>();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        if (prototypes.Index<EntityPrototype>(id).TryGetComponent<IconComponent>(out var icon))
            result.Add(new TguiData().String("url", Frame(icon.Icon)).String("color", "#ffffff"));
        else if (prototypes.Index<EntityPrototype>(id).TryGetComponent<SpriteComponent>(out var sprite))
        {
            // Prototypes already deserialize sprite layers. Starting arbitrary
            // catalogue entities also starts their gameplay systems and can fail.
            result.AddRange(SpriteImages(sprite));
        }
        _items[id] = result;
        return result;
    }
}
