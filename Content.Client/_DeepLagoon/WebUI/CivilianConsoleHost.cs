using Robust.Client.UserInterface;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Shared civilian terminal surface with a typed native-window fallback.</summary>
public sealed class CivilianConsoleHost : Control
{
    public TguiPanel Panel { get; }
    private float _readyWait;
    private bool _fallbackPending;

    public CivilianConsoleHost(Action restoreNative)
    {
        HorizontalExpand = VerticalExpand = true;
        Panel = new TguiPanel(inheritChatAppearance: false) { ConsoleKeyboardFeedback = true };
        AddChild(Panel);
        Panel.SetNativeFallback(() =>
        {
            Visible = false;
            restoreNative();
        });
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!VisibleInTree || Panel.UsingNative || _fallbackPending) return;
        if (Panel.Web.IsReady) { _readyWait = 0; return; }
        if ((_readyWait += args.DeltaSeconds) < 8) return;
        _fallbackPending = true;
        UserInterfaceManager.DeferAction(() =>
        {
            if (Disposed) return;
            _fallbackPending = false;
            if (VisibleInTree) Panel.SwitchToNative();
        });
    }
}
