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
    // The browser defines this aperture. Native contents must not resize their own host.
    public LayoutContainer ProgramLayer { get; } = new() { MouseFilter = MouseFilterMode.Ignore, InheritChildMeasure = false };
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
        var show = visible && bounds.Z > 0 && bounds.W > 0;
        if (program.Visible != show) program.Visible = show;
        if (!program.Visible)
            return;
        var scale = UIScale;
        var position = new Vector2(MathF.Round(bounds.X * Size.X * scale), MathF.Round(bounds.Y * Size.Y * scale)) / scale;
        var end = new Vector2(MathF.Round((bounds.X + bounds.Z) * Size.X * scale), MathF.Round((bounds.Y + bounds.W) * Size.Y * scale)) / scale;
        var size = Vector2.Max(Vector2.Zero, end - position);
        // Use requested bounds, not the arranged position: minimum sizes can shift the
        // latter and must not feed another offset back into the browser's next layout.
        if (program.GetValue<float>(LayoutContainer.MarginLeftProperty) != position.X) LayoutContainer.SetMarginLeft(program, position.X);
        if (program.GetValue<float>(LayoutContainer.MarginTopProperty) != position.Y) LayoutContainer.SetMarginTop(program, position.Y);
        if (program.GetValue<float>(LayoutContainer.MarginRightProperty) != end.X) LayoutContainer.SetMarginRight(program, end.X);
        if (program.GetValue<float>(LayoutContainer.MarginBottomProperty) != end.Y) LayoutContainer.SetMarginBottom(program, end.Y);
        // SetSize invalidates measurement even when assigned the same value.
        if (program.SetSize != size) program.SetSize = size;
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
