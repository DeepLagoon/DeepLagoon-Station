using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client.PDA;

/// <summary>The browser paints the tablet; the original cartridge control owns its input and state.</summary>
public sealed class PdaOrbitalHost : Control
{
    public TguiPanel Panel { get; } = new(inheritChatAppearance: false, captureKeyboard: true, showWindowControls: false);
    public LayoutContainer ProgramLayer { get; } = new() { MouseFilter = MouseFilterMode.Ignore };
    public Vector4 ProgramViewport { get; private set; }
    public event Action<float>? Frame;
    private float _readyWait;
    private bool _fallbackPending;

    public PdaOrbitalHost()
    {
        HorizontalExpand = VerticalExpand = true;
        AddChild(Panel);
        AddChild(ProgramLayer);
        Panel.Web.Ready += ResetViewport;
    }

    public void ResetViewport() => ProgramViewport = Vector4.Zero;

    public bool SetViewport(TguiActionData args)
    {
        if (!args.TryFloat("x", out var x) || !args.TryFloat("y", out var y) ||
            !args.TryFloat("w", out var w) || !args.TryFloat("h", out var h) ||
            x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1.001f || y + h > 1.001f)
            return false;
        ProgramViewport = new Vector4(x, y, w, h);
        return true;
    }

    public void LayoutProgram(Control program, bool visible)
    {
        var bounds = ProgramViewport;
        program.Visible = visible && bounds.Z > 0 && bounds.W > 0;
        if (!program.Visible)
            return;
        LayoutContainer.SetPosition(program, new Vector2(bounds.X * Size.X, bounds.Y * Size.Y));
        program.SetSize = new Vector2(bounds.Z * Size.X, bounds.W * Size.Y);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!VisibleInTree || Panel.UsingNative)
            return;
        Frame?.Invoke(args.DeltaSeconds);
        if (Panel.Web.IsReady)
        {
            _readyWait = 0;
            return;
        }
        if ((_readyWait += args.DeltaSeconds) < 8 || _fallbackPending)
            return;
        _fallbackPending = true;
        UserInterfaceManager.DeferAction(() =>
        {
            _fallbackPending = false;
            if (!Disposed && VisibleInTree)
                Panel.SwitchToNative();
        });
    }
}
