using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Timing;

namespace Content.Client.Audio.Jukebox;

/// <summary>The document paints the horseshoe itself, without a rectangular native window frame.</summary>
public sealed class JukeboxTguiWindow : BaseWindow
{
    // The cabinet intentionally opts out of the automatic minimize/maximize/close buttons.
    public readonly TguiPanel Panel = new(suspendWhenHidden: true, inheritChatAppearance: false, captureKeyboard: true, showWindowControls: false);
    public event Action<float>? Frame;
    public JukeboxTguiWindow()
    {
        MinSize = new Vector2(360, 540);
        SetSize = new Vector2(440, 730);
        AddChild(Panel);
        // A native overlay keeps the resize grip above CEF's input surface.
        AddChild(new Control
        {
            MinSize = new Vector2(16, 20), HorizontalAlignment = HAlignment.Right,
            VerticalAlignment = VAlignment.Bottom, MouseFilter = MouseFilterMode.Pass,
            ToolTip = Loc.GetString("dl-jukebox-resize"),
        });
    }
    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Frame?.Invoke(args.DeltaSeconds);
    }
    protected override DragMode GetDragModeFor(Vector2 point)
    {
        if (point.X > Size.X - 14 && point.Y > Size.Y - 20) return DragMode.Bottom | DragMode.Right;
        return DragMode.None;
    }
}
