using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Shuttles.UI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Shuttles;

/// <summary>Shared ship-console presentation. The existing radar owns all map/input behavior.</summary>
public sealed class ShipRadarConsoleHost : Control
{
    public TguiPanel Tgui { get; }
    private readonly BaseShuttleControl _radar;
    private readonly LayoutContainer _layer;
    private readonly ShuttleRadarGlassControl _glass;
    private readonly Func<TguiData> _state;
    private string _kind;
    public string Kind => _kind;
    private readonly string _title;
    private Vector4 _viewport;
    private float _publishTimer;

    public ShipRadarConsoleHost(ShuttleNavControl radar, string kind, string title,
        Func<TguiData> state, Action<TguiActionData> weaponAction, Action close, Action restoreNative)
    {
        _radar = radar;
        _kind = kind;
        _title = title;
        _state = state;
        HorizontalExpand = VerticalExpand = true;
        var nativeParent = radar.Parent!;
        var nativeSiblings = new List<(Control Control, bool Visible)>();
        for (Control? ancestor = nativeParent; ancestor != null && ancestor is not Robust.Client.UserInterface.CustomControls.BaseWindow; ancestor = ancestor.Parent)
            nativeSiblings.Add((ancestor, ancestor.Visible));
        var nativeMargin = radar.Margin;
        var nativeMinimum = radar.MinSize;
        // Size has not been arranged yet here and may be zero. Preserve the layout constraint.
        var nativeSize = radar.SetSize;
        var nativeLayoutMargins = new Thickness(
            radar.GetValue<float>(LayoutContainer.MarginLeftProperty),
            radar.GetValue<float>(LayoutContainer.MarginTopProperty),
            radar.GetValue<float>(LayoutContainer.MarginRightProperty),
            radar.GetValue<float>(LayoutContainer.MarginBottomProperty));
        var nativeFit = radar.FitViewport;
        radar.Orphan();
        radar.Margin = new Thickness(0);
        radar.MinSize = Vector2.Zero;
        radar.FitViewport = true;
        radar.Visible = false;
        Tgui = new TguiPanel(inheritChatAppearance: false) { ConsoleKeyboardFeedback = kind != "handheld", InterfaceStyleKey = "ShipRadarConsole." + kind };
        AddChild(Tgui);
        _layer = new LayoutContainer { MouseFilter = MouseFilterMode.Ignore };
        AddChild(_layer);
        _layer.AddChild(radar);
        _glass = new ShuttleRadarGlassControl { Visible = false };
        _layer.AddChild(_glass);
        Tgui.OnClose += close;
        Tgui.SetNativeFallback(() =>
        {
            if (radar is CombatShuttleNavControl combat) combat.StopFiring();
            radar.Orphan();
            radar.FitViewport = nativeFit;
            radar.Margin = nativeMargin;
            radar.MinSize = nativeMinimum;
            radar.SetSize = nativeSize;
            LayoutContainer.SetMarginLeft(radar, nativeLayoutMargins.Left);
            LayoutContainer.SetMarginTop(radar, nativeLayoutMargins.Top);
            LayoutContainer.SetMarginRight(radar, nativeLayoutMargins.Right);
            LayoutContainer.SetMarginBottom(radar, nativeLayoutMargins.Bottom);
            radar.Visible = true;
            Visible = false;
            foreach (var (control, visible) in nativeSiblings) control.Visible = visible;
            restoreNative();
        });
        Tgui.SetTguiRestore(() =>
        {
            radar.Orphan();
            radar.Margin = new Thickness(0);
            radar.MinSize = Vector2.Zero;
            radar.FitViewport = true;
            _layer.AddChild(radar);
            // Native ancestors belong to the old surface. The shared host remains visible.
            foreach (var (control, _) in nativeSiblings)
                if (control != Parent) control.Visible = false;
            Visible = true;
            _viewport = Vector4.Zero;
            radar.Visible = _glass.Visible = false;
            Publish();
        });
        Tgui.OnAction += (action, payload) =>
        {
            if (!TguiActionData.TryParse(payload, out var args)) return;
            if (action == "viewport")
            {
                if (!TryViewport(args!, out var bounds)) return;
                _viewport = bounds;
                radar.Visible = _glass.Visible = true;
                return;
            }
            if (action == "close") { close(); return; }
            if (action == "weapon" && _kind == "gunnery") weaponAction(args!);
            if (action == "radar")
            {
                switch (args!.String("id"))
                {
                    case "iff": radar.ShowIFF ^= true; break;
                    case "details": radar.ShowIFFDetailed ^= true; break;
                    case "docks" when _kind == "gunnery": radar.ShowDocks ^= true; break;
                }
            }
            Publish();
        };
        Publish();
    }

    public void SetHandheld(bool handheld)
    {
        if (_kind == "gunnery") return;
        _kind = handheld ? "handheld" : "mass";
        Tgui.InterfaceStyleKey = "ShipRadarConsole." + _kind;
        Tgui.ConsoleKeyboardFeedback = !handheld;
        _glass.ShowHardware = !handheld;
        Publish();
    }

    public static bool TryViewport(TguiActionData args, out Vector4 bounds)
    {
        bounds = default;
        if (!args.TryFloat("x", out var x) || !args.TryFloat("y", out var y) ||
            !args.TryFloat("w", out var w) || !args.TryFloat("h", out var h) ||
            x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1.001f || y + h > 1.001f)
            return false;
        bounds = new Vector4(x, y, w, h);
        return true;
    }

    public void Publish()
    {
        if (Tgui.UsingNative) return;
        var radar = (ShuttleNavControl)_radar;
        Tgui.SetIncrementalState("ShipRadarConsole", _state().String("kind", _kind)
            .Object("nav", new TguiData().Bool("iff", radar.ShowIFF).Bool("details", radar.ShowIFFDetailed)
                .Bool("docks", radar.ShowDocks).Number("range", radar.WorldRange)), _title);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!VisibleInTree || Tgui.UsingNative) return;
        var position = new Vector2(_viewport.X * Size.X, _viewport.Y * Size.Y);
        var size = new Vector2(_viewport.Z * Size.X, _viewport.W * Size.Y);
        LayoutContainer.SetPosition(_radar, position);
        _radar.SetSize = size;
        LayoutContainer.SetPosition(_glass, position);
        _glass.PanelTextureOffset = position;
        _glass.SetSize = size;
        _publishTimer -= args.DeltaSeconds;
        if (_publishTimer > 0) return;
        _publishTimer = 0.2f;
        Publish();
    }
}
