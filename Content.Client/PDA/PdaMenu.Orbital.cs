using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.CartridgeLoader;
using Content.Shared.PDA;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.PDA;

public sealed partial class PdaMenu
{
    public PdaOrbitalHost? OrbitalHost { get; private set; }
    public bool OrbitalActive { get; private set; }
    public event Action<string>? OnDeviceAction;
    private PdaUpdateState? _orbitalState;
    private readonly Dictionary<string, (EntityUid Uid, CartridgeComponent Component)> _orbitalPrograms = new();
    private readonly List<(Control Control, bool Visible)> _classicChildren = new();
    private Vector2 _classicMinimum;
    private Vector2 _programSize;
    private bool _programClip;
    private float _orbitalRefresh;
    private bool _orbitalInitialized;
    private static readonly string[] OrbitalLabels =
    [
        "overview", "programs", "settings", "shift", "time", "identity", "no-id", "unassigned", "owner",
        "station", "alert", "balance", "ship", "date", "war", "address", "empty", "open", "install", "uninstall",
        "installed", "cartridge", "readonly", "flashlight", "on", "off", "slot-id", "slot-pen", "slot-pai",
        "ringtone", "music", "uplink", "lock-uplink", "close-program", "close", "resize", "waiting", "active-program",
    ];

    /// <summary>Capture the native presentation after the PDA's entity-specific colors are applied.</summary>
    public void InitializeOrbital()
    {
        if (_orbitalInitialized)
            return;
        _orbitalInitialized = true;
        _classicMinimum = MinSize;
        _programSize = ProgramView.SetSize;
        _programClip = ProgramView.RectClipContent;
        var style = WindowInterfaceStyle.Attach(this, "Pda", () => OrbitalActive, tgui =>
        {
            WindowInterfaceStyle.Save("Pda", tgui);
            if (tgui)
                OpenOrbital();
            else
                OrbitalHost?.Panel.SwitchToNative();
        });
        style.Margin = new Thickness(0, 2, 34, 0);
        if (WindowInterfaceStyle.UseTgui("Pda"))
            OpenOrbital();
    }

    private void OpenOrbital()
    {
        if (OrbitalHost != null)
        {
            OrbitalHost.Panel.SwitchToTgui();
            ShowOrbital();
            return;
        }
        foreach (var child in Children)
            _classicChildren.Add((child, child.Visible));
        var host = new PdaOrbitalHost();
        OrbitalHost = host;
        AddChild(host);
        host.Panel.OnClose += Close;
        host.Panel.OnAction += OrbitalAction;
        host.Panel.SetNativeFallback(RestoreClassicPda);
        host.Panel.SetTguiRestore(ShowOrbital);
        var styleAnchor = new Control
        {
            SetSize = new Vector2(48, 24), HorizontalAlignment = HAlignment.Right,
            VerticalAlignment = VAlignment.Top, Margin = new Thickness(0, 17, 53, 0),
            MouseFilter = MouseFilterMode.Ignore,
        };
        host.AddChild(styleAnchor);
        host.AddChild(new Control
        {
            MinSize = new Vector2(14, 20), HorizontalAlignment = HAlignment.Right,
            VerticalAlignment = VAlignment.Bottom, MouseFilter = MouseFilterMode.Pass,
            ToolTip = Loc.GetString("dl-pda-resize"),
        });
        host.Panel.BindStyle(styleAnchor, "Pda", () => OrbitalActive, tgui =>
        {
            if (tgui) OpenOrbital();
            else host.Panel.SwitchToNative();
        });
        host.Frame += seconds =>
        {
            host.LayoutProgram(ProgramView, _currentView == ProgramContentView && ProgramTitle.Visible);
            if ((_orbitalRefresh += seconds) < 1)
                return;
            _orbitalRefresh = 0;
            PublishOrbital();
        };
        var labels = new TguiData();
        foreach (var key in OrbitalLabels)
            labels.String(key, Loc.GetString("dl-pda-" + key));
        host.Panel.SetStaticData(new TguiData().Object("labels", labels).ToString());
        SetSize = new Vector2(760, 680);
        ShowOrbital();
    }

    private void ShowOrbital()
    {
        if (OrbitalHost is not { } host)
            return;
        OrbitalActive = true;
        MinSize = new Vector2(520, 440);
        foreach (var (child, _) in _classicChildren)
            child.Visible = false;
        ProgramView.Orphan();
        ProgramView.Margin = new Thickness(0);
        ProgramView.MinSize = Vector2.Zero;
        ProgramView.RectClipContent = true;
        LayoutContainer.SetAnchorPreset(ProgramView, LayoutContainer.LayoutPreset.TopLeft);
        host.ProgramLayer.AddChild(ProgramView);
        host.Visible = true;
        host.ResetViewport();
        ProgramView.Visible = false;
        PublishOrbital();
    }

    private void RestoreClassicPda()
    {
        if (!OrbitalActive || OrbitalHost is not { } host)
            return;
        OrbitalActive = false;
        ProgramView.Orphan();
        ProgramView.SetSize = _programSize;
        ProgramView.RectClipContent = _programClip;
        LayoutContainer.SetPosition(ProgramView, Vector2.Zero);
        ViewContainer.AddChild(ProgramView);
        ProgramView.SetPositionInParent(ProgramContentView);
        host.Visible = false;
        MinSize = _classicMinimum;
        foreach (var (child, visible) in _classicChildren)
            child.Visible = visible;
        HideAllViews();
        ChangeView(_currentView);
        HomeButton.IsCurrent = _currentView == HomeView;
        ProgramListButton.IsCurrent = _currentView == ProgramListView;
        SettingsButton.IsCurrent = _currentView == SettingsView;
        ProgramTitle.IsCurrent = _currentView == ProgramContentView;
    }

    private void UpdateOrbitalPrograms(List<(EntityUid, CartridgeComponent)> programs)
    {
        _orbitalPrograms.Clear();
        foreach (var (uid, component) in programs)
            _orbitalPrograms[uid.ToString()] = (uid, component);
        PublishOrbital();
    }

    private void PublishOrbital()
    {
        if (!OrbitalActive || OrbitalHost is not { } host)
            return;
        host.LayoutProgram(ProgramView, _currentView == ProgramContentView && ProgramTitle.Visible);
        var state = _orbitalState;
        var info = state?.PdaOwnerInfo ?? default;
        var time = _gameTiming.CurTime.Subtract(_gameTicker.RoundStartTimeSpan).ToString("hh\\:mm\\:ss");
        var data = new TguiData().Bool("ready", state != null).Number("view", _currentView)
            .String("time", time).String("owner", info.ActualOwnerName).String("idOwner", info.IdOwner)
            .String("job", info.JobTitle).String("company", info.CompanyName).String("companyColor", info.CompanyColor.ToHex())
            .String("station", state?.StationName).String("alert", _alertLevel).String("instructions", _instructions)
            .String("alertColor", info.StationAlertColor.ToHex()).String("war", info.WarLevel)
            .String("balance", _balance).String("ship", state?.OwnedShipName).String("date", info.CurrentDate?.ToString("dd/MM/yyyy"))
            .String("address", state?.Address?.ToUpperInvariant()).Bool("hasId", EjectIdButton.IsActive)
            .Bool("hasPen", state?.HasPen == true).Bool("hasPai", state?.HasPai == true)
            .Bool("flashlight", state?.FlashlightEnabled == true).Bool("canPlayMusic", state?.CanPlayMusic == true)
            .Bool("hasUplink", state?.HasUplink == true).String("programTitle", ProgramTitle.Visible ? ProgramTitle.LabelText : "")
            .Array("programs", _orbitalPrograms.Select(entry => new TguiData().String("id", entry.Key)
                .String("name", Loc.GetString(entry.Value.Component.ProgramName))
                .String("kind", entry.Value.Component.ProgramName)
                .Bool("installed", entry.Value.Component.InstallationStatus != InstallationStatus.Cartridge)
                .Bool("canInstall", entry.Value.Component.InstallationStatus == InstallationStatus.Cartridge)
                .Bool("canUninstall", entry.Value.Component.InstallationStatus == InstallationStatus.Installed && !entry.Value.Component.Readonly)));
        host.Panel.SetIncrementalState("Pda", data, Loc.GetString("dl-pda-overview"));
    }

    private void OrbitalAction(string action, string payload)
    {
        if (!OrbitalActive || OrbitalHost is not { } host || !TguiActionData.TryParse(payload, out var args) || args == null)
            return;
        if (action == "close") { Close(); return; }
        if (action == "viewport")
        {
            if (_currentView == ProgramContentView && ProgramTitle.Visible && host.SetViewport(args))
                host.LayoutProgram(ProgramView, true);
            return;
        }
        if (action == "view" && args.TryInt("view", out var view) && view is >= HomeView and <= ProgramContentView)
        {
            if (view == ProgramContentView && !ProgramTitle.Visible)
                return;
            host.ResetViewport();
            ChangeView(view);
            return;
        }
        if (_orbitalState is not { } state)
            return;
        switch (action)
        {
            case "program" or "install" or "uninstall":
                if (args.String("id") is not { } id || !_orbitalPrograms.TryGetValue(id, out var program))
                    return;
                if (action == "program") OnProgramItemPressed?.Invoke(program.Uid);
                else if (action == "install" && program.Component.InstallationStatus == InstallationStatus.Cartridge)
                    OnInstallButtonPressed?.Invoke(program.Uid);
                else if (action == "uninstall" && program.Component.InstallationStatus == InstallationStatus.Installed && !program.Component.Readonly)
                    OnUninstallButtonPressed?.Invoke(program.Uid);
                break;
            case "flashlight" or "ringtone": OnDeviceAction?.Invoke(action); break;
            // Empty slot buttons insert the item held in the active hand, just like the native UI.
            case "id" or "pen" or "pai": OnDeviceAction?.Invoke(action); break;
            case "music" when state.CanPlayMusic: OnDeviceAction?.Invoke(action); break;
            case "uplink" or "lockUplink" when state.HasUplink: OnDeviceAction?.Invoke(action); break;
            case "closeProgram" when ProgramTitle.Visible:
                OnDeviceAction?.Invoke(action);
                HideProgramHeader();
                ToHomeScreen();
                break;
            case "copy":
                var value = args.String("field") switch
                {
                    "owner" => _pdaOwner, "id" => _owner + ", " + _jobTitle, "station" => _stationName,
                    "alert" => _alertLevel, "instructions" => _instructions, "balance" => _balance,
                    "ship" => _shuttleDeed, "date" => _currentDate, "address" => state.Address,
                    "time" => _gameTiming.CurTime.Subtract(_gameTicker.RoundStartTimeSpan).ToString("hh\\:mm\\:ss"),
                    _ => null,
                };
                if (value != null) _clipboard.SetText(value);
                break;
        }
    }

    protected override DragMode GetDragModeFor(Vector2 point)
    {
        if (!OrbitalActive)
            return base.GetDragModeFor(point);
        return point.X > Size.X - 14 && point.Y > Size.Y - 20 ? DragMode.Bottom | DragMode.Right : DragMode.None;
    }
}
