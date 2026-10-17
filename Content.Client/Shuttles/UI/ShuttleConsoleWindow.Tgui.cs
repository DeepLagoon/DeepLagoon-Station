using System.Linq;
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
    private ShuttleRadarGlassControl _radarGlass = default!;
    private Control[] _views = Array.Empty<Control>();
    private Vector4 _viewport;
    private float _publishTimer;
    private Content.Shared.Exodus.ShipShields.ShipShieldState? _shieldState;

    public void UpdateShieldState(Content.Shared.Exodus.ShipShields.ShipShieldState? state)
    {
        _shieldState = state;
        _publishTimer = 0;
    }

    private TguiData ShieldTguiState() => _shieldState is { } state
        ? new TguiData().Bool("present", true).Bool("active", state.Active).Bool("powered", state.Powered)
            .Bool("recharging", state.Recharging).Number("health", state.HealthFraction)
            .Number("overload", Math.Max(0f, state.OverloadAccumulator))
            .Number("draw", state.BaseDraw + state.Draw).Number("maxDraw", state.BaseDraw + state.MaxDraw)
        : new TguiData().Bool("present", false);

    private void InitializeTgui()
    {
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
        foreach (var view in _views)
        {
            view.Orphan();
            view.Margin = new Thickness(0);
            view.MinSize = Vector2.Zero;
            ((Content.Client.UserInterface.Controls.MapGridControl)view).FitViewport = true;
            _radarLayer.AddChild(view);
        }
        // Last sibling also covers labels/buttons outside the circular aperture.
        _radarGlass = new ShuttleRadarGlassControl();
        _radarLayer.AddChild(_radarGlass);
        Tgui.OnAction += TguiAction;
        UpdateTguiMode();
        PublishTgui();
    }

    private void UpdateTguiMode()
    {
        if (_mode != ShuttleConsoleMode.Nav)
            ((Content.Client._DeepLagoon.Shuttles.CombatShuttleNavControl)CombatRadar).StopFiring();
        for (var i = 0; i < _views.Length; i++)
            _views[i].Visible = i == (int)_mode && _viewport.Z > 0 && _viewport.W > 0;
        _radarGlass.Visible = _viewport.Z > 0 && _viewport.W > 0;
        _publishTimer = 0;
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
        if (action == "mode" && args!.TryInt("value", out var mode) && mode is >= 0 and <= 2)
            SwitchMode((ShuttleConsoleMode)mode);
        else if (action == "close") Close();
        else if (action == "nav") NavContainer.TguiAction(args!);
        else if (action == "weapon") CombatPanel.TguiAction(args!);
        else if (action == "map") MapContainer.TguiAction(args!);
        else if (action == "dock") DockContainer.TguiAction(args!);
        PublishTgui();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!VisibleInTree) return;
        var size = ContentsContainer.Size;
        foreach (var view in _views)
        {
            LayoutContainer.SetPosition(view, new Vector2(_viewport.X * size.X, _viewport.Y * size.Y));
            view.SetSize = new Vector2(_viewport.Z * size.X, _viewport.W * size.Y);
        }
        LayoutContainer.SetPosition(_radarGlass, new Vector2(_viewport.X * size.X, _viewport.Y * size.Y));
        _radarGlass.SetSize = new Vector2(_viewport.Z * size.X, _viewport.W * size.Y);
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
            .Number("mode", (int)_mode)
            .Object("shield", ShieldTguiState())
            .Object("nav", NavContainer.TguiState())
            .Object("weapons", CombatPanel.TguiState())
            .Object("map", MapContainer.TguiState())
            .Object("dock", DockContainer.TguiState()), Title ?? "Shuttle console");
    }
}
