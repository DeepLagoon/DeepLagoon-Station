using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Music;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Music;

public sealed class MusicTguiWindow : BaseWindow
{
    public readonly TguiPanel Panel = new(suspendWhenHidden: false, inheritChatAppearance: false, captureKeyboard: true, showWindowControls: false);
    public event Action<float>? Frame;

    public MusicTguiWindow()
    {
        MinSize = new Vector2(380, 480);
        SetSize = new Vector2(460, 640);
        AddChild(Panel);
        AddChild(new Control
        {
            MinSize = new Vector2(16, 20), HorizontalAlignment = HAlignment.Right,
            VerticalAlignment = VAlignment.Bottom, MouseFilter = MouseFilterMode.Pass,
            ToolTip = Loc.GetString("dl-music-title"),
        });
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Frame?.Invoke(args.DeltaSeconds);
    }
}
