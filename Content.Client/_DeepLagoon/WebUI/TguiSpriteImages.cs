using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.Loadouts;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Read first sprite frames from packaged resources, without GPU readbacks.</summary>
public sealed class TguiSpriteImages
{
    private readonly Dictionary<SpriteSpecifier, string> _frames = new();
    private readonly Dictionary<string, List<TguiData>> _items = new();
    private readonly Dictionary<(ResPath, byte), string> _tileFrames = new();
    public void Reset() { _frames.Clear(); _items.Clear(); _tileFrames.Clear(); }

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
            var packed = false;
            if (!resources.TryContentFileRead(path, out var stream) && spec is SpriteSpecifier.Rsi packedRsi)
            {
                var root = new ResPath("/Textures") / packedRsi.RsiPath;
                stream = resources.ContentFileRead(root.WithExtension("rsic"));
                packed = true;
            }
            if (stream == null) return _frames[spec] = "";
            using var input = stream;
            using var image = Image.Load<Rgba32>(stream);
            using var output = new MemoryStream();
            if (spec is SpriteSpecifier.Rsi rsi)
            {
                if (packed)
                    return _frames[spec] = PackedFrame(image,
                        resources.GetResource<RSIResource>(new ResPath("/Textures") / rsi.RsiPath).RSI, rsi.RsiState);
                var size = resources.GetResource<RSIResource>(new ResPath("/Textures") / rsi.RsiPath).RSI.Size;
                using var frame = image.Clone(context => context.Crop(new Rectangle(0, 0, size.X, size.Y)));
                frame.SaveAsPng(output);
            }
            else image.SaveAsPng(output);
            return _frames[spec] = "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception) { return _frames[spec] = ""; }
    }

    public static string PackedFrame(Image<Rgba32> image, RSI rsi, string stateName)
    {
        // The first south frame of the first state is at sheet index zero.
        // All states share a GPU atlas offset; its minimum X/Y is the RSI origin.
        // Subtract it to recover coordinates inside this RSI's packed PNG.
        if (!rsi.TryGetState(stateName, out var selected))
            throw new InvalidDataException("RSIC state not found: " + stateName);
        var regions = rsi.Select(state => ((AtlasTexture) state.Frame0).SubRegion).ToArray();
        var region = ((AtlasTexture) selected.Frame0).SubRegion;
        var rectangle = new Rectangle((int) (region.Left - regions.Min(r => r.Left)),
            (int) (region.Top - regions.Min(r => r.Top)), rsi.Size.X, rsi.Size.Y);
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
