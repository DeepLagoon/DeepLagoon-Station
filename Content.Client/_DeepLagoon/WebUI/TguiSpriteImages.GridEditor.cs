using System.Numerics;
using Content.Shared.Atmos.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization.TypeSerializers.Implementations;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class TguiSpriteImages
{
    private readonly Dictionary<string, List<TguiData>> _gridEditorItems = new();

    public IEnumerable<TguiData> GridEditorItemImages(EntityPrototype prototype, SpriteComponent sprite)
    {
        if (_gridEditorItems.TryGetValue(prototype.ID, out var cached))
            return cached;

        prototype.TryComp<AtmosPipeLayersComponent>(out var pipe, IoCManager.Resolve<IComponentFactory>());
        if (pipe?.PipeLayersLocked == true)
            pipe = null;

        var layerPaths = new Dictionary<int, Dictionary<AtmosPipeLayer, string>>();
        if (pipe != null)
        {
            var reflection = IoCManager.Resolve<IReflectionManager>();
            foreach (var (key, paths) in pipe.SpriteLayersRsiPaths)
            {
                var layerKey = reflection.TryParseEnumReference(key, out var parsed) ? (object) parsed : key;
                // This is prototype data, which also exists without an initialized SpriteSystem.
                if (sprite.LayerMapTryGet(layerKey, out var index))
                    layerPaths[index] = paths;
            }
        }

        var result = new List<TguiData>();
        for (var index = 0; index < sprite.LayerData.Count; index++)
        {
            var layer = sprite.LayerData[index];
            var state = ((ISpriteLayer) layer).RsiState;
            if (!layer.Visible || layer.CopyToShaderParameters != null || layer.ActualRsi is not { } rsi || !state.IsValid)
                continue;

            var paths = layerPaths.GetValueOrDefault(index);
            var basePaths = layer.RSI == null ? pipe?.SpriteRsiPaths : null;
            RSI RsiForLayer(AtmosPipeLayer pipeLayer)
            {
                // An explicit sprite-layer RSI takes precedence over the inherited base RSI.
                if (paths != null && paths.TryGetValue(pipeLayer, out var path) && LoadEditorRsi(path) is { } layerRsi)
                    return layerRsi;
                if (basePaths != null && basePaths.TryGetValue(pipeLayer, out path) && LoadEditorRsi(path) is { } baseRsi)
                    return baseRsi;
                return rsi;
            }

            var defaultRsi = pipe != null ? RsiForLayer(pipe.CurrentPipeLayer) : rsi;
            var image = EditorFrames(defaultRsi, state, sprite, layer);
            var matrix = Matrix3x2.Multiply(layer.LocalMatrix, sprite.LocalMatrix);
            image.String("color", (layer.Color * sprite.Color).ToHexNoAlpha()).Bool("native", true)
                .Number("m11", matrix.M11).Number("m12", matrix.M12).Number("m21", matrix.M21).Number("m22", matrix.M22)
                .Number("m31", matrix.M31).Number("m32", matrix.M32);

            if (sprite.GranularLayersRendering && layer.RenderingStrategy != LayerRenderingStrategy.UseSpriteStrategy)
                image.String("rotationMode", layer.RenderingStrategy.ToString());

            if (pipe != null && (paths != null || basePaths is { Count: > 0 }))
            {
                var variants = new List<TguiData>();
                for (var i = 0; i < pipe.NumberOfPipeLayers; i++)
                {
                    var target = RsiForLayer((AtmosPipeLayer) i);
                    if (target != defaultRsi)
                        variants.Add(EditorFrames(target, state, sprite, layer).Number("layer", i));
                }
                if (variants.Count > 0)
                    image.Array("pipeFrames", variants);
            }

            result.Add(image);
        }

        if (result.Count == 0)
            result.AddRange(Item(prototype.ID));
        return _gridEditorItems[prototype.ID] = result;
    }

    private static RSI? LoadEditorRsi(string path)
    {
        var resources = IoCManager.Resolve<IResourceCache>();
        return resources.TryGetResource(SpriteSpecifierSerializer.TextureRoot / path, out RSIResource? resource)
            ? resource.RSI
            : null;
    }

    private TguiData EditorFrames(RSI rsi, RSI.StateId stateId, SpriteComponent sprite, SpriteComponent.Layer layer)
    {
        var stateName = stateId.Name!;
        var image = new TguiData().Number("width", rsi.Size.X).Number("height", rsi.Size.Y);
        if (!rsi.TryGetState(stateId, out var state) || state.RsiDirections == RsiDirectionType.Dir1)
            return image.String("url", Frame(new SpriteSpecifier.Rsi(rsi.Path, stateName)));

        var count = state.RsiDirections == RsiDirectionType.Dir4 ? 4 : 8;
        string FrameAt(int index)
        {
            var direction = sprite.EnableDirectionOverride
                ? sprite.DirectionOverride.Convert(state.RsiDirections)
                : ((Direction) (index * 8 / count)).Convert(state.RsiDirections);
            if (state.RsiDirections == RsiDirectionType.Dir4)
                direction = direction.OffsetRsiDir(layer.DirOffset);
            return DirectionalFrame(rsi.Path, stateName, direction);
        }

        var frames = new List<TguiData>(count - 1);
        for (var i = 1; i < count; i++)
            frames.Add(new TguiData().String("url", FrameAt(i)));
        return image.String("url", FrameAt(0)).Array("frames", frames);
    }
}
