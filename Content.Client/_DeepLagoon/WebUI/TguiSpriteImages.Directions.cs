using System.IO;
using System.Linq;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using YamlDotNet.RepresentationModel;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class TguiSpriteImages
{
    private readonly Dictionary<(ResPath Path, string State, RsiDirection Direction), string> _directionalFrames = new();

    /// <summary>Read a directional frame using sandbox-safe metadata and the engine's loaded RSI atlas.</summary>
    public string DirectionalFrame(ResPath path, string state, RsiDirection direction)
    {
        var key = (path, state, direction);
        if (_directionalFrames.TryGetValue(key, out var cached)) return cached;
        var resources = IoCManager.Resolve<IResourceCache>();
        var root = new ResPath("/Textures") / path;
        try
        {
            var rsi = resources.GetResource<RSIResource>(root).RSI;
            var selected = rsi[state];
            var packed = !resources.TryContentFileRead(root / (state + ".png"), out var stream);
            using var input = stream;
            var sheet = packed ? Sheet(resources, root) : null;
            using var unpacked = packed ? null : Image.Load<Rgba32>(input!);
            var image = sheet?.Image ?? unpacked!;
            Rectangle rectangle;
            if (packed)
            {
                var frameDirection = selected.RsiDirections == RsiDirectionType.Dir1 ? RsiDirection.South : direction;
                var region = ((AtlasTexture) selected.GetFrame(frameDirection, 0)).SubRegion;
                rectangle = new Rectangle((int) region.Left - sheet!.Left,
                    (int) region.Top - sheet.Top, rsi.Size.X, rsi.Size.Y);
            }
            else
            {
                // JSON manifests are also valid YAML. Use the content-approved parser.
                using var reader = resources.ContentFileReadText(root / "meta.json");
                var metadata = new YamlStream(); metadata.Load(reader);
                var states = (YamlSequenceNode) ((YamlMappingNode) metadata.Documents[0].RootNode)["states"];
                var entry = states.Children.Cast<YamlMappingNode>().First(node => ((YamlScalarNode) node["name"]).Value == state);
                var offset = 0;
                if (entry.FirstOrDefault(pair => pair.Key is YamlScalarNode { Value: "delays" }).Value is YamlSequenceNode delays)
                {
                    var directions = delays.Children;
                    for (var dir = 0; dir < Math.Min((int) direction, directions.Count - 1); dir++)
                        offset += Math.Max(1, ((YamlSequenceNode) directions[dir]).Children.Count);
                }
                else if (entry.FirstOrDefault(pair => pair.Key is YamlScalarNode { Value: "directions" }).Value is YamlScalarNode count)
                    offset = Math.Min((int) direction, int.Parse(count.Value!) - 1);
                var columns = image.Width / rsi.Size.X;
                rectangle = new Rectangle(offset % columns * rsi.Size.X, offset / columns * rsi.Size.Y, rsi.Size.X, rsi.Size.Y);
            }
            using var frame = image.Clone(context => context.Crop(rectangle));
            using var output = new MemoryStream(); frame.SaveAsPng(output);
            return _directionalFrames[key] = "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception) { return _directionalFrames[key] = ""; }
    }
}
