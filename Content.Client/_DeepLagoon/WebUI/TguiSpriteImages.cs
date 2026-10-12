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
public sealed class TguiSpriteImages
{
    /// <summary>
    /// One cache of decoded frames for every TGUI window of the session. Decoding is the costly part, and
    /// per-window caches decoded the same sprites again (the character editor is rebuilt on every lobby entry).
    /// </summary>
    public static TguiSpriteImages Shared { get; private set; } = new();

    /// <summary>Drops every decoded frame, after prototypes were reloaded.</summary>
    public static void ResetShared() => Shared = new TguiSpriteImages();

    private readonly Dictionary<SpriteSpecifier, string> _frames = new();
    private readonly Dictionary<string, List<TguiData>> _items = new();
    private readonly Dictionary<ResPath, string> _tileFrames = new();

    // Released builds pack each RSI into one sheet (.rsic). A state build asks for many states of the
    // same RSI in a row, so a decoded sheet is kept until the next tick instead of decoded per state.
    private readonly Dictionary<ResPath, PackedSheet> _sheets = new();

    private sealed record PackedSheet(Image<Rgba32> Image, RSI Rsi, int Left, int Top);

    /// <summary>Tile sprites can be variant strips. Catalogue icons use the first 32×32 frame.</summary>
    public string TileFrame(ResPath path)
    {
        if (_tileFrames.TryGetValue(path, out var cached)) return cached;
        try
        {
            using var input = IoCManager.Resolve<IResourceCache>().ContentFileRead(new ResPath("/Textures") / path);
            using var image = Image.Load<Rgba32>(input);
            using var frame = image.Clone(context => context.Crop(new Rectangle(0, 0, Math.Min(32, image.Width), Math.Min(32, image.Height))));
            using var output = new MemoryStream(); frame.SaveAsPng(output);
            return _tileFrames[path] = "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception) { return _tileFrames[path] = ""; }
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
        // A broken sprite stays broken: do not decode it again on every publish.
        catch (Exception) { return _frames[spec] = ""; }
    }

    private PackedSheet Sheet(IResourceCache resources, ResPath rsiPath)
    {
        if (_sheets.TryGetValue(rsiPath, out var sheet)) return sheet;
        var rsi = resources.GetResource<RSIResource>(rsiPath).RSI;
        Image<Rgba32> image;
        using (var input = resources.ContentFileRead(rsiPath.WithExtension("rsic")))
            image = Image.Load<Rgba32>(input);
        var (left, top) = Origin(rsi);
        sheet = new PackedSheet(image, rsi, left, top);
        if (_sheets.Count == 0) Timer.Spawn(0, ReleaseSheets);
        _sheets[rsiPath] = sheet;
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

    // The first south frame of the first state is at sheet index zero.
    // All states share a GPU atlas offset; its minimum X/Y is the RSI origin.
    // Subtract it to recover coordinates inside this RSI's packed PNG.
    private static (int Left, int Top) Origin(RSI rsi)
    {
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
