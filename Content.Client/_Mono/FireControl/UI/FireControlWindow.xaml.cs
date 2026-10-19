// Copyright Rane (elijahrane@gmail.com) 2025
// All rights reserved. Relicensed under AGPL with permission
using System.Numerics;
using Content.Client._DeepLagoon.Shuttles;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._Mono.FireControl;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._Mono.FireControl.UI;

public sealed class FireControlWindow : FancyWindow
{
    private readonly FireControlPanel _panel;
    public FireControlNavControl Radar { get; } = new CombatShuttleNavControl();
    public ShipRadarConsoleHost? ConsoleHost { get; }
    public Dictionary<NetEntity, Button> WeaponsList => _panel.WeaponsList;
    public event Action? OnServerRefresh { add => _panel.OnServerRefresh += value; remove => _panel.OnServerRefresh -= value; }
    public event Action? OnWeaponSelectionChanged { add => _panel.OnWeaponSelectionChanged += value; remove => _panel.OnWeaponSelectionChanged -= value; }

    public FireControlWindow()
    {
        Title = Loc.GetString("gunnery-window-title");
        SetSize = new Vector2(1000, 800);
        MinSize = new Vector2(800, 600);
        _panel = new FireControlPanel(Radar);
        var layout = new BoxContainer { HorizontalExpand = true, VerticalExpand = true };
        layout.AddChild(_panel);
        Radar.HorizontalExpand = Radar.VerticalExpand = true;
        layout.AddChild(Radar);
        AddChild(layout);
        {
            ConsoleHost = new ShipRadarConsoleHost(Radar, "gunnery", Title ?? "Контроль орудий",
                () => new TguiData().Object("weapons", _panel.TguiState()).Object("shield", _panel.ShieldTguiState()),
                _panel.TguiAction, Close, () =>
                {
                    layout.AddChild(Radar);
                    _panel.SynchronizeRadarControls();
                    layout.Visible = true;
                });
            layout.Visible = false;
            ContentsContainer.AddChild(ConsoleHost);
        }
    }

    public void UpdateStatus(FireControlConsoleBoundInterfaceState state)
    {
        _panel.UpdateStatus(state);
        ConsoleHost?.Publish();
    }
}
