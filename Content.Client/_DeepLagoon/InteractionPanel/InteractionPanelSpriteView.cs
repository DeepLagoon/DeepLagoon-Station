using System.Numerics;
using System.Linq;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.InteractionPanel;

/// <summary>Живое превью с отдельным stencil-буфером для масок одежды.</summary>
public sealed class InteractionPanelSpriteView : SpriteView
{
    private IRenderTexture? _target;
    private readonly Dictionary<SpriteComponent.Layer, ShaderInstance> _clothingShaders = new();

    protected override void Draw(IRenderHandle handle)
    {
        if (PixelSize.X <= 0 || PixelSize.Y <= 0) return;
        if (_target == null || _target.Size != PixelSize)
        {
            _target?.Dispose();
            _target = IoCManager.Resolve<IClyde>().CreateRenderTarget(PixelSize,
                new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb, hasDepthStencil: true),
                name: nameof(InteractionPanelSpriteView));
        }

        // Слои displacement содержат карту формы одежды, а не отдельную картинку.
        // Сохраняем их, но используем отдельные UI-шейдеры: параметры направления,
        // освещение и stencil мира не должны влиять на увеличенное превью.
        handle.RenderInRenderTarget(_target, () =>
        {
            handle.SetScissor(null);
            handle.DrawingHandleScreen.SetTransform(Matrix3x2.Identity);
            DrawClothing(handle);
        }, Color.Transparent);
        handle.DrawingHandleScreen.DrawTexture(_target.Texture, Vector2.Zero);
    }

    private void DrawClothing(IRenderHandle handle)
    {
        var layers = Sprite?.AllLayers.OfType<SpriteComponent.Layer>()
            .Where(layer => layer.ShaderPrototype == "DisplacedStencilDraw").ToArray()
            ?? Array.Empty<SpriteComponent.Layer>();
        foreach (var stale in _clothingShaders.Keys.Except(layers).ToArray())
        {
            _clothingShaders[stale].Dispose();
            _clothingShaders.Remove(stale);
        }

        var originals = new List<(SpriteComponent.Layer Layer, ShaderInstance? Shader)>();
        try
        {
            foreach (var layer in layers)
            {
                if (!_clothingShaders.TryGetValue(layer, out var shader))
                {
                    shader = IoCManager.Resolve<IPrototypeManager>()
                        .Index<ShaderPrototype>("DeepLagoonInteractionPanelDisplacement").InstanceUnique();
                    _clothingShaders.Add(layer, shader);
                }
                originals.Add((layer, layer.Shader));
                layer.Shader = shader;
            }
            // CopyToShaderParameters заполняет карту и UV отдельно для каждого слоя.
            base.Draw(handle);
        }
        finally
        {
            // Подмена действует только внутри синхронной отрисовки UI.
            // Мир продолжает использовать исходные шейдеры и их параметры.
            foreach (var (layer, shader) in originals) layer.Shader = shader;
        }
    }

    protected override void ExitedTree()
    {
        _target?.Dispose();
        _target = null;
        foreach (var shader in _clothingShaders.Values) shader.Dispose();
        _clothingShaders.Clear();
        base.ExitedTree();
    }
}
