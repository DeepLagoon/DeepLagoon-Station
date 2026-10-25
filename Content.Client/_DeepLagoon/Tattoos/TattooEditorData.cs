using System.Globalization;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Tattoos;
using Content.Shared.Humanoid;
using Robust.Client.GameObjects;
using Robust.Client.Utility;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Prototypes;
using System.Text;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Robust.Shared.Configuration;

namespace Content.Client._DeepLagoon.Tattoos;

/// <summary>Shared TGUI state/actions for the profile editor and the in-round machine.</summary>
public sealed class TattooEditorData
{
    private readonly Dictionary<string, string> _frames = new();
    private readonly Dictionary<string, string> _bodyPreviews = new();
    private readonly StringBuilder _bake = new();
    private string? _bakeId;
    private int _chunkIndex;
    private int _chunkCount;

    public bool ReceiveBakeChunk(TguiActionData args, out TguiActionData? completed)
    {
        completed = null;
        if (!args.TryInt("index", out var index) || !args.TryInt("count", out var count)
            || count is < 1 or > 100 || args.String("id") is not { } id
            || args.String("text") is not { } text || text.Length > 4000) return false;
        if (index == 0) { _bake.Clear(); _bakeId = id; _chunkIndex = 0; _chunkCount = count; }
        if (id != _bakeId || index != _chunkIndex || count != _chunkCount || _bake.Length + text.Length > Tattoo.MaxBakePayload)
        { _bake.Clear(); _bakeId = null; return false; }
        _bake.Append(text); _chunkIndex++;
        if (_chunkIndex < count) return true;
        var payload = _bake.ToString(); _bake.Clear(); _bakeId = null;
        return TguiActionData.TryParse(payload, out completed, Tattoo.MaxBakePayload) && completed!.String("id") == id;
    }
    public static string PartName(HumanoidVisualLayers part) => part switch
    {
        HumanoidVisualLayers.Chest => "Грудь", HumanoidVisualLayers.Head => "Голова",
        HumanoidVisualLayers.LArm => "Левая рука", HumanoidVisualLayers.RArm => "Правая рука",
        HumanoidVisualLayers.LLeg => "Левая нога", HumanoidVisualLayers.RLeg => "Правая нога",
        HumanoidVisualLayers.LHand => "Левая кисть", HumanoidVisualLayers.RHand => "Правая кисть",
        HumanoidVisualLayers.LFoot => "Левая стопа", _ => "Правая стопа",
    };

    public TguiData Build(EntityUid target, List<Tattoo> tattoos, string status = "", bool machine = false)
    {
        var banned = IoCManager.Resolve<Content.Client.Players.PlayTimeTracking.JobRequirementsManager>().TattoosBanned;
        if (banned) status = TattooAccess.BanMessage;
        var entities = IoCManager.Resolve<IEntityManager>();
        var data = new TguiData().String("status", status).Bool("machine", machine).Bool("banned", banned)
            .Bool("externalPngAllowed", TattooFileDialogs.CanImportExternalPng)
            .Bool("disabled", banned || !IoCManager.Resolve<IConfigurationManager>().GetCVar(TattooCVars.Enabled))
            .Number("maxTattoos", Tattoo.MaxTattoos).Number("maxStrokes", Tattoo.MaxStrokes)
            .Number("maxPoints", Tattoo.MaxPoints).Number("maxLayers", Tattoo.MaxLayers)
            .Number("maxBitmapChars", Tattoo.MaxBitmapChars).Number("maxTattooBitmapChars", Tattoo.MaxTattooBitmapChars)
            .Array("tattoos", tattoos.Select(ToData));
        if (!entities.TryGetComponent<HumanoidAppearanceComponent>(target, out var humanoid)
            || !entities.TryGetComponent<SpriteComponent>(target, out var sprite)) return data;
        if (machine && !humanoid.TattoosEnabled) data.Bool("disabled", true);
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        data.Array("parts", Tattoo.BodyParts.Where(part => TattooBody.Allows(humanoid, part, prototypes)).Select(part =>
        {
            var frames = new List<TguiData>();
            if (TattooBodyPreview.TrySource(humanoid, sprite, prototypes, part, out var index))
            {
                var anatomyLayers = TattooBodyPreview.ContourLayers(humanoid, sprite, prototypes, part,
                    entities.System<Content.Client._DeepLagoon.BodyCustomization.BodyCustomizationVisualizerSystem>().LayerIndices(target, sprite, part));
                for (var direction = 0; direction < 4; direction++)
                {
                    var key = TattooBodyPreview.Key(sprite, new[] { index }.Concat(anatomyLayers), direction);
                    if (!_frames.TryGetValue(key, out var png))
                    {
                        try { using var frame = TattooBodyPreview.ReadSkin(sprite, index, direction, anatomyLayers); png = TattooSpriteFrames.Png(frame); }
                        catch (Exception) { png = ""; }
                        if (_frames.Count >= 128) _frames.Clear();
                        _frames[key] = png;
                    }
                    var markingLayers = TattooBodyPreview.MarkingLayers(humanoid, sprite, prototypes, part, visibleOnly: true);
                    var previewKey = TattooBodyPreview.Key(sprite, new[] { index }.Concat(markingLayers).Concat(anatomyLayers), direction);
                    if (!_bodyPreviews.TryGetValue(previewKey, out var preview))
                    {
                        try { using var frame = TattooBodyPreview.Render(sprite, index, markingLayers, direction, anatomyLayers); preview = TattooSpriteFrames.Png(frame); }
                        catch (Exception) { preview = ""; }
                        if (_bodyPreviews.Count >= 128) _bodyPreviews.Clear();
                        _bodyPreviews[previewKey] = preview;
                    }
                    frames.Add(new TguiData().Number("direction", direction).String("image", png).String("previewImage", preview)
                        .Object("placement", Placement((SpriteComponent.Layer)sprite[index], direction)));
                }
            }
            return new TguiData().String("id", part.ToString()).String("name", PartName(part))
                .String("color", sprite.LayerMapTryGet(part, out var layer) ? sprite[layer].Color.ToHex() : humanoid.SkinColor.ToHex())
                .Bool("covered", humanoid.HiddenLayers.ContainsKey(part)).Array("frames", frames);
        }));
        return data;
    }

    private static TguiData Placement(SpriteComponent.Layer layer, int direction)
    {
        var state = layer.ActualState!;
        layer.GetLayerDrawMatrix(unchecked((RsiDirection)(state.RsiDirections == RsiDirectionType.Dir1 ? 0 : direction)).OffsetRsiDir(layer.DirOffset), out var matrix);
        var a = matrix.M11 * state.Size.X / Robust.Client.Graphics.EyeManager.PixelsPerMeter;
        var b = -matrix.M12 * state.Size.X / Robust.Client.Graphics.EyeManager.PixelsPerMeter;
        var c = -matrix.M21 * state.Size.Y / Robust.Client.Graphics.EyeManager.PixelsPerMeter;
        var d = matrix.M22 * state.Size.Y / Robust.Client.Graphics.EyeManager.PixelsPerMeter;
        return new TguiData().Number("a", a).Number("b", b).Number("c", c).Number("d", d)
            .Number("e", .5f + matrix.M31 - (a + c) / 2).Number("f", .5f - matrix.M32 - (b + d) / 2);
    }

    private static TguiData ToData(Tattoo t) => new TguiData().String("id", t.Id).String("part", t.BodyPart.ToString())
        .String("name", PartName(t.BodyPart)).Number("direction", t.Direction).Number("x", t.X).Number("y", t.Y)
        .Number("scale", t.Scale).Number("rotation", t.Rotation).Number("opacity", t.Opacity).Bool("mirrored", t.Mirrored)
        .String("normalBitmap", t.NormalBitmap).String("glowBitmap", t.GlowBitmap).String("artworkProof", t.ArtworkProof)
        .Array("strokes", t.Strokes.Select(s => new TguiData().String("color", s.Color).Number("width", s.Width)
            .Bool("glow", s.Glow).Bool("erase", s.Erase).String("points", TattooVisualizerSystem.Points(s))));

    public static bool TryApply(string action, TguiActionData args, List<Tattoo> current, out List<Tattoo> next, out string status)
    {
        next = Tattoo.Sanitize(current);
        status = "";
        if (action == "tattoo-clear") { next.Clear(); return true; }
        var tattoo = next.FirstOrDefault(t => t.Id == args.String("id"));
        if (action == "tattoo-add")
        {
            if (next.Count >= Tattoo.MaxTattoos) { status = "Достигнут лимит татуировок."; return false; }
            if (!Enum.TryParse<HumanoidVisualLayers>(args.String("part"), out var part) || !Tattoo.BodyParts.Contains(part)
                || !args.TryInt("direction", out var direction) || direction is < 0 or > 3) return false;
            next.Add(new Tattoo { BodyPart = part, Direction = direction });
            return true;
        }
        if (tattoo == null) return false;
        switch (action)
        {
            case "tattoo-remove": next.Remove(tattoo); return true;
            case "tattoo-mirror":
                tattoo.X = 1 - tattoo.X;
                tattoo.Rotation = -tattoo.Rotation;
                if (tattoo.NormalBitmap.Length > 0 || tattoo.GlowBitmap.Length > 0 || tattoo.Strokes.Count == 0)
                    tattoo.Mirrored = !tattoo.Mirrored;
                foreach (var mirroredStroke in tattoo.Strokes)
                    mirroredStroke.Points = mirroredStroke.Points.Select(p => new TattooPoint(1 - p.X, p.Y)).ToList();
                return true;
            case "tattoo-bake":
                // Placement of an unfinished drawing is committed together with its flattened ink.
                if (args.String("x") != null)
                {
                    if (!args.TryFloat("x", out var bakedX) || !args.TryFloat("y", out var bakedY)
                        || !args.TryFloat("scale", out var bakedScale) || !args.TryFloat("rotation", out var bakedRotation)
                        || !args.TryFloat("opacity", out var bakedOpacity)) return false;
                    tattoo.X = bakedX; tattoo.Y = bakedY; tattoo.Scale = bakedScale;
                    tattoo.Rotation = bakedRotation; tattoo.Opacity = bakedOpacity;
                    if (!tattoo.IsValid()) return false;
                }
                var normal = args.String("normal") ?? "";
                var luminous = args.String("glow") ?? "";
                if (!TattooBitmap.ValidPng(normal) || !TattooBitmap.ValidPng(luminous)
                    || normal.Length + luminous.Length > Tattoo.MaxTattooBitmapChars)
                { status = "Изображение слишком большое или повреждено."; return false; }
                try
                {
                    foreach (var png in new[] { normal, luminous }.Where(p => p.Length > 0))
                    { using var input = new MemoryStream(Convert.FromBase64String(png)); using var decoded = Image.Load<Rgba32>(input); }
                }
                catch (Exception) { status = "Не удалось прочитать изображение."; return false; }
                tattoo.NormalBitmap = normal; tattoo.GlowBitmap = luminous; tattoo.Strokes.Clear();
                return tattoo.IsValid();
            case "tattoo-undo":
                if (tattoo.Strokes.Count == 0) return false;
                tattoo.Strokes.RemoveAt(tattoo.Strokes.Count - 1); return true;
            case "tattoo-copy":
                if (next.Count >= Tattoo.MaxTattoos || !args.TryInt("direction", out var copiedDirection) || copiedDirection is < 0 or > 3) return false;
                var copy = tattoo.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Direction = copiedDirection;
                next.Add(copy); return true;
            case "tattoo-transform":
                if (!args.TryFloat("x", out var x) || !args.TryFloat("y", out var y) || !args.TryFloat("scale", out var scale)
                    || !args.TryFloat("rotation", out var rotation) || !args.TryFloat("opacity", out var opacity)) return false;
                tattoo.X = x; tattoo.Y = y; tattoo.Scale = scale; tattoo.Rotation = rotation; tattoo.Opacity = opacity;
                return tattoo.IsValid();
            case "tattoo-stroke":
                var encoded = args.String("points");
                if (encoded == null || encoded.Length > 4096 || !args.TryFloat("width", out var width)) return false;
                var stroke = new TattooStroke
                {
                    Color = args.String("color") ?? "", Width = width,
                    Glow = args.String("glow") == "true", Erase = args.String("erase") == "true",
                };
                foreach (var point in encoded.Split(';'))
                {
                    var coordinates = point.Split(',');
                    if (stroke.Points.Count >= Tattoo.MaxStrokePoints || coordinates.Length != 2
                        || !float.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                        || !float.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) return false;
                    stroke.Points.Add(new TattooPoint(px, py));
                }
                if (!stroke.IsValid()) return false;
                tattoo.Strokes.Add(stroke);
                if (tattoo.IsValid()) return true;
                status = "Достигнут лимит штрихов или точек. Удалите лишние штрихи.";
                return false;
            default: return false;
        }
    }
}
