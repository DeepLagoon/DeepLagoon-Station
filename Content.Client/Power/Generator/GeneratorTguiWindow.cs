using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client.Power.Generator;

public sealed class GeneratorTguiWindow : FancyWindow
{
    public readonly TguiPanel Panel = new(inheritChatAppearance: false, captureKeyboard: true);
    public event Action<float>? Frame;

    public GeneratorTguiWindow()
    {
        SetSize = new Vector2(470, 740);
        MinSize = new Vector2(340, 450);
        ContentsContainer.AddChild(Panel);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Frame?.Invoke(args.DeltaSeconds);
    }
}
