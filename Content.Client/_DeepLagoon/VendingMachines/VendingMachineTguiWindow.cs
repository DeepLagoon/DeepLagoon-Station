using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.VendingMachines;

public sealed class VendingMachineTguiWindow : FancyWindow
{
    public readonly TguiPanel Panel = new(inheritChatAppearance: false);
    public event Action<float>? Frame;

    public VendingMachineTguiWindow()
    {
        SetSize = new Vector2(760, 700);
        MinSize = new Vector2(420, 420);
        ContentsContainer.AddChild(Panel);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Frame?.Invoke(args.DeltaSeconds);
    }
}
