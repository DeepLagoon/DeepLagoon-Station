using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Economy;

/// <summary>Draw the packaged icon/layers directly. Catalogue previews never start entity gameplay systems.</summary>
public sealed class TradeItemPreview : Control
{
    private readonly List<(Texture Texture, Color Color)> _layers = new();
    public int LayerCount => _layers.Count;
    public void SetPrototype(string id)
    {
        _layers.Clear();
        if (!IoCManager.Resolve<IPrototypeManager>().TryIndex<EntityPrototype>(id, out var prototype)) return;
        if (prototype.TryGetComponent<IconComponent>(out var icon))
            _layers.Add((IoCManager.Resolve<IEntityManager>().System<SpriteSystem>().GetIcon(icon), Color.White));
        else if (prototype.TryGetComponent<SpriteComponent>(out var sprite))
            foreach (var layer in sprite.AllLayers)
            {
                if (!layer.Visible) continue;
                if (layer.Texture != null) _layers.Add((layer.Texture, layer.Color * sprite.Color));
                else if (layer.ActualRsi is { } rsi && layer.RsiState.IsValid && rsi.TryGetState(layer.RsiState, out var state))
                    _layers.Add((state.Frame0, layer.Color * sprite.Color));
            }
    }
    protected override void Draw(DrawingHandleScreen handle)
    {
        foreach (var (texture, color) in _layers)
        {
            var scale = Math.Min((float)PixelWidth / texture.Width, (float)PixelHeight / texture.Height);
            var size = new Vector2(texture.Width, texture.Height) * scale;
            handle.DrawTextureRect(texture, UIBox2.FromDimensions((new Vector2(PixelWidth, PixelHeight) - size) / 2, size), color);
        }
    }
}
