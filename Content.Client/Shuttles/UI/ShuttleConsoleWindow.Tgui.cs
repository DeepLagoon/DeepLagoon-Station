using System.Linq;
using Content.Shared.Shuttles.BUIStates;
using System.Numerics;
using Content.Client._DeepLagoon.Shuttles;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client.Shuttles.UI;

public sealed partial class ShuttleConsoleWindow
{
    public TguiPanel Tgui { get; private set; } = default!;
    private LayoutContainer _radarLayer = default!;
    private bool _statusMode;
    private ShuttleHullNavMapControl _hullMap = default!;
    public event Action? ResetHullHistory;
    private TguiData _hullState = new TguiData().Bool("present", false);
    private Control[] _views = Array.Empty<Control>();
    private Vector4 _viewport;
    private float _publishTimer;
    private Content.Shared.Exodus.ShipShields.ShipShieldState? _shieldState;
    private readonly List<(Control View, Control Parent, int Index, Thickness Margin, Vector2 Min, Vector2 Size, Thickness LayoutMargins, Thickness Anchors, bool Fit)> _nativeViews = new();
    private bool _nativeConsole;
    private Vector2 _nativeMinimum;

    public void UpdateShieldState(Content.Shared.Exodus.ShipShields.ShipShieldState? state)
    {
        _shieldState = state;
        _publishTimer = 0;
    }

    public void UpdateHullState(ShuttleHullState? state)
    {
        _hullMap.Hull = state;
        _hullState = state == null
            ? new TguiData().Bool("present", false)
            : new TguiData().Bool("present", true)
                .Number("integrity", state.IntegrityFraction)
                .String("singleMissingName", state.SingleMissingName ?? string.Empty)
                .Number("workingThrusters", state.WorkingThrusters).Number("workingGyroscopes", state.WorkingGyroscopes)
                .Number("storedEnergy", state.StoredEnergy).Number("energyCapacity", state.EnergyCapacity)
                .Object("thrusters", HullCount(state.ExpectedThrusters, state.MissingThrusters))
                .Object("weapons", HullCount(state.ExpectedWeapons, state.MissingWeapons))
                .Object("structures", HullCount(state.ExpectedStructures, state.MissingStructures))
                .Object("tiles", HullCount(state.ExpectedTiles, state.MissingTiles));
        _publishTimer = 0;
    }

    private static TguiData HullCount(int expected, int missing) =>
        new TguiData().Number("expected", expected).Number("missing", missing);

    private TguiData ShieldTguiState() => _shieldState is { } state
        ? new TguiData().Bool("present", true).Bool("active", state.Active).Bool("powered", state.Powered)
            .Bool("recharging", state.Recharging).Number("health", state.HealthFraction)
            .Number("overload", Math.Max(0f, state.OverloadAccumulator))
            .Number("draw", state.BaseDraw + state.Draw).Number("maxDraw", state.BaseDraw + state.MaxDraw)
        : new TguiData().Bool("present", false);

    private void InitializeTgui()
    {
        _nativeMinimum = MinSize;
        SetSize = new Vector2(1060, 740);
        MinSize = new Vector2(760, 520);
        // Native controls remain the state/command owners. Only their graphics viewports
        // are reparented above CEF; no framebuffer copies or browser-side weapon simulation.
        ContentsContainer.GetChild(0).Visible = false;
        _views = new Control[] { CombatRadar, MapContainer.TguiRadar, DockContainer.TguiRadar };
        Tgui = new TguiPanel(inheritChatAppearance: false);
        ContentsContainer.AddChild(Tgui);
        _radarLayer = new LayoutContainer { MouseFilter = Control.MouseFilterMode.Ignore };
        ContentsContainer.AddChild(_radarLayer);
        _hullMap = new ShuttleHullNavMapControl { FitViewport = true, MinSize = Vector2.Zero, Visible = false };
        _radarLayer.AddChild(_hullMap);
        foreach (var view in _views)
        {
            _nativeViews.Add((view, view.Parent!, view.GetPositionInParent(), view.Margin, view.MinSize, view.SetSize,
                new Thickness(view.GetValue<float>(LayoutContainer.MarginLeftProperty),
                    view.GetValue<float>(LayoutContainer.MarginTopProperty),
                    view.GetValue<float>(LayoutContainer.MarginRightProperty),
                    view.GetValue<float>(LayoutContainer.MarginBottomProperty)),
                new Thickness(view.GetValue<float>(LayoutContainer.AnchorLeftProperty),
                    view.GetValue<float>(LayoutContainer.AnchorTopProperty),
                    view.GetValue<float>(LayoutContainer.AnchorRightProperty),
                    view.GetValue<float>(LayoutContainer.AnchorBottomProperty)),
                ((Content.Client.UserInterface.Controls.MapGridControl) view).FitViewport));
            view.Orphan();
            view.Margin = new Thickness(0);
            view.MinSize = Vector2.Zero;
            LayoutContainer.SetAnchorPreset(view, LayoutContainer.LayoutPreset.TopLeft);
            ((Content.Client.UserInterface.Controls.MapGridControl)view).FitViewport = true;
            _radarLayer.AddChild(view);
        }
        Tgui.OnAction += TguiAction;
        Tgui.OnClose += Close;
        Tgui.SetNativeFallback(RestoreNativeConsole);
        Tgui.SetTguiRestore(() =>
        {
            _nativeConsole = false;
            MinSize = new Vector2(760, 520);
            ContentsContainer.GetChild(0).Visible = false;
            Tgui.Visible = _radarLayer.Visible = true;
            foreach (var view in _views)
            {
                view.Orphan();
                view.Margin = new Thickness(0);
                view.MinSize = Vector2.Zero;
                LayoutContainer.SetAnchorPreset(view, LayoutContainer.LayoutPreset.TopLeft);
                ((Content.Client.UserInterface.Controls.MapGridControl) view).FitViewport = true;
                _radarLayer.AddChild(view);
            }
            _viewport = Vector4.Zero;
            UpdateTguiMode();
            PublishTgui();
        });
        UpdateTguiMode();
        PublishTgui();
    }

    private void UpdateTguiMode()
    {
        if (_nativeConsole) return;
        if (_statusMode || _mode != ShuttleConsoleMode.Nav)
            ((Content.Client._DeepLagoon.Shuttles.CombatShuttleNavControl)CombatRadar).StopFiring();
        for (var i = 0; i < _views.Length; i++)
            _views[i].Visible = !_statusMode && i == (int)_mode && _viewport.Z > 0 && _viewport.W > 0;
        _hullMap.Visible = _statusMode && _viewport.Z > 0 && _viewport.W > 0;
        _publishTimer = 0;
    }

    private void RestoreNativeConsole()
    {
        if (_nativeConsole) return;
        _nativeConsole = true;
        _statusMode = false;
        MinSize = _nativeMinimum;
        ((CombatShuttleNavControl)CombatRadar).StopFiring();
        Tgui.Visible = _radarLayer.Visible = false;
        foreach (var (view, parent, index, margin, min, size, layoutMargins, anchors, fit) in _nativeViews)
        {
            view.Orphan();
            parent.AddChild(view);
            view.SetPositionInParent(index);
            view.Margin = margin;
            view.MinSize = min;
            view.SetSize = size;
            LayoutContainer.SetAnchorLeft(view, anchors.Left);
            LayoutContainer.SetAnchorTop(view, anchors.Top);
            LayoutContainer.SetAnchorRight(view, anchors.Right);
            LayoutContainer.SetAnchorBottom(view, anchors.Bottom);
            LayoutContainer.SetMarginLeft(view, layoutMargins.Left);
            LayoutContainer.SetMarginTop(view, layoutMargins.Top);
            LayoutContainer.SetMarginRight(view, layoutMargins.Right);
            LayoutContainer.SetMarginBottom(view, layoutMargins.Bottom);
            view.Visible = true;
            ((Content.Client.UserInterface.Controls.MapGridControl)view).FitViewport = fit;
        }
        ContentsContainer.GetChild(0).Visible = true;
        SetupMode(_mode);
    }

    private void TguiAction(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var args)) return;
        if (action == "viewport" && args!.TryFloat("x", out var x) && args.TryFloat("y", out var y)
            && args.TryFloat("w", out var w) && args.TryFloat("h", out var h))
        {
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1.001f || y + h > 1.001f) return;
            _viewport = new Vector4(x, y, w, h);
            UpdateTguiMode();
            return;
        }
        if (action == "mode" && args!.TryInt("value", out var mode) && mode is >= 0 and <= 3)
        {
            _statusMode = mode == 3;
            if (!_statusMode)
                SwitchMode((ShuttleConsoleMode)mode);
            UpdateTguiMode();
        }
        else if (action == "close") Close();
        else if (action == "resetHullHistory") ResetHullHistory?.Invoke();
        else if (action == "nav") NavContainer.TguiAction(args!);
        else if (action == "weapon") CombatPanel.TguiAction(args!);
        else if (action == "map") MapContainer.TguiAction(args!);
        else if (action == "dock") DockContainer.TguiAction(args!);
        PublishTgui();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!VisibleInTree || _nativeConsole) return;
        var size = ContentsContainer.Size;
        LayoutContainer.SetPosition(_hullMap, new Vector2(_viewport.X * size.X, _viewport.Y * size.Y));
        _hullMap.SetSize = new Vector2(_viewport.Z * size.X, _viewport.W * size.Y);
        foreach (var view in _views)
        {
            LayoutContainer.SetPosition(view, new Vector2(_viewport.X * size.X, _viewport.Y * size.Y));
            view.SetSize = new Vector2(_viewport.Z * size.X, _viewport.W * size.Y);
        }
        _publishTimer -= args.DeltaSeconds;
        if (_publishTimer > 0) return;
        _publishTimer = 0.2f;
        PublishTgui();
    }

    private void PublishTgui()
    {
        if (Tgui == null) return;
        NavContainer.RefreshTelemetry();
        Tgui.SetIncrementalState("ShuttleConsole", new TguiData()
            .Number("mode", _statusMode ? 3 : (int)_mode)
            .Object("hull", _hullState)
            .Object("shield", ShieldTguiState())
            .Object("nav", NavContainer.TguiState())
            .Object("weapons", CombatPanel.TguiState())
            .Object("map", MapContainer.TguiState())
            .Object("dock", DockContainer.TguiState()), Title ?? "Shuttle console");
    }
}
