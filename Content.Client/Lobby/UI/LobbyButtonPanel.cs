using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Original NovaSector lobby backplate and button positions, in native sprite pixels.
/// The ready/join slot is slightly wider than the 151px backplate, as in the source HUD.
/// </summary>
public sealed class LobbyButtonPanel : Container
{
    public const float BackplateWidth = 302;
    public const float LargeWidth = 320;
    public const float CompactHeight = 123;
    private readonly Texture _background;

    public LobbyButtonPanel()
    {
        RectClipContent = true;
        _background = IoCManager.Resolve<IResourceCache>()
            .GetResource<RSIResource>("/Textures/_DeepLagoon/Interface/Lobby/background.rsi").RSI["background"].Frame0;
    }

    private static int ScaleFor(Vector2 size) => size.X >= LargeWidth && size.Y >= CompactHeight * 2 ? 2 : 1;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var scale = ScaleFor(availableSize);
        for (var i = 0; i < ChildCount; i++)
            GetChild(i).Measure(Slot(i, scale).Size);
        return new Vector2(LargeWidth / 2, CompactHeight) * scale;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        var scale = ScaleFor(finalSize);
        for (var i = 0; i < ChildCount; i++)
            GetChild(i).Arrange(Slot(i, scale));
        return finalSize;
    }

    private static UIBox2 Slot(int index, int scale)
    {
        var box = index switch
        {
            0 => new UIBox2(0, 8, 160, 36),
            1 => new UIBox2(11, 40, 150, 62),
            _ => new UIBox2(11, 70, 150, 107),
        };
        return new UIBox2(box.TopLeft * scale, box.BottomRight * scale);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var scale = ScaleFor(Size) * UIScale;
        var size = _background.Size * scale;
        handle.DrawTextureRect(_background, UIBox2.FromDimensions(new Vector2((PixelWidth - size.X) / 2, 0), size));
    }
}
