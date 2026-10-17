using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client.Power.Battery;

public sealed class BatteryTguiWindow : FancyWindow
{
    public readonly TguiPanel Panel = new(inheritChatAppearance: false, captureKeyboard: true);
    public event Action<float>? Frame;

    public BatteryTguiWindow()
    {
        SetSize = new Vector2(680, 700);
        MinSize = new Vector2(400, 450);
        ContentsContainer.AddChild(Panel);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Frame?.Invoke(args.DeltaSeconds);
    }
}
