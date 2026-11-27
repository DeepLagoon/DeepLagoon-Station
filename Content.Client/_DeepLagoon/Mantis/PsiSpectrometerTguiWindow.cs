using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Mantis;

public sealed class PsiSpectrometerTguiWindow : BaseWindow
{
    public readonly TguiPanel Panel = new(suspendWhenHidden: false, inheritChatAppearance: false, showWindowControls: false);
    public event Action<float>? Frame;

    public PsiSpectrometerTguiWindow()
    {
        MinSize = new Vector2(380, 420);
        SetSize = new Vector2(460, 480);
        AddChild(Panel);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Frame?.Invoke(args.DeltaSeconds);
    }
}
