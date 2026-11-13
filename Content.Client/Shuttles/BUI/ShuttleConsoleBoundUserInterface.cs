using Content.Client.Shuttles.UI;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Events;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Shared.Log;
using Robust.Shared.Map;

// Mono
using Content.Shared._Mono.Shuttles;
using Content.Shared._Mono.FireControl;
using System.Linq;

namespace Content.Client.Shuttles.BUI;

[UsedImplicitly]
public sealed partial class ShuttleConsoleBoundUserInterface : BoundUserInterface // Frontier: added partial
{
    [ViewVariables]
    private ShuttleConsoleWindow? _window;

    public ShuttleConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<ShuttleConsoleWindow>();

        _window.RequestFTL += OnFTLRequest;
        _window.ResetHullHistory += () => SendMessage(new ShuttleHullResetMessage());
        _window.HullViewChanged += active => SendMessage(new ShuttleHullViewMessage(active));
        _window.RequestBeaconFTL += OnFTLBeaconRequest;
        _window.RequestAutopilot += OnAutopilotRequest; // Mono
        _window.DockRequest += OnDockRequest;
        _window.UndockRequest += OnUndockRequest;
        _window.UndockAllRequest += OnUndockAllRequest;
        _window.ToggleFTLLockRequest += OnToggleFTLLockRequest;
        _window.CombatPanel.OnServerRefresh += () => SendMessage(new FireControlConsoleRefreshServerMessage());
        _window.CombatPanel.OnWeaponSelectionChanged += UpdateSelectedWeapons;
        _window.CombatRadar.OnRadarClick += coordinates =>
        {
            var position = EntMan.GetNetCoordinates(coordinates);
            if (_window.CombatRadar.IsMouseDown())
            {
                var selected = _window.CombatPanel.WeaponsList.Where(entry => entry.Value.Pressed).Select(entry => entry.Key).ToList();
                if (selected.Count != 0)
                    SendMessage(new FireControlConsoleFireMessage(selected, position));
            }
            else
                SendMessage(new FireControlAimMessage(position));
        };
        NfOpen(); // Frontier
    }

    private void OnToggleFTLLockRequest(List<NetEntity> dockEntities, bool enabled)
    {
        Logger.GetSawmill("shuttle").Debug( $"ShuttleConsoleBUI: Sending FTL lock request with enabled={enabled}, entities={string.Join(", ", dockEntities)}");
        SendMessage(new ToggleFTLLockRequestMessage(dockEntities, enabled));
    }

    private void UpdateSelectedWeapons()
    {
        if (_window == null)
            return;
        _window.CombatRadar.UpdateSelectedWeapons(_window.CombatPanel.WeaponsList
            .Where(entry => entry.Value.Pressed).Select(entry => entry.Key).ToHashSet());
    }

    private void OnUndockAllRequest(List<NetEntity> dockEntities)
    {
        SendMessage(new UndockAllRequestMessage(dockEntities));
    }

    private void OnUndockRequest(NetEntity entity)
    {
        SendMessage(new UndockRequestMessage()
        {
            DockEntity = entity,
        });
    }

    private void OnDockRequest(NetEntity entity, NetEntity target)
    {
        SendMessage(new DockRequestMessage()
        {
            DockEntity = entity,
            TargetDockEntity = target,
        });
    }

    private void OnFTLBeaconRequest(NetEntity ent, Angle angle)
    {
        SendMessage(new ShuttleConsoleFTLBeaconMessage()
        {
            Beacon = ent,
            Angle = angle,
        });
    }

    private void OnFTLRequest(MapCoordinates obj, Angle angle)
    {
        SendMessage(new ShuttleConsoleFTLPositionMessage()
        {
            Coordinates = obj,
            Angle = angle,
        });
    }

    // Mono
    private void OnAutopilotRequest(MapCoordinates obj, Angle angle)
    {
        SendMessage(new ShuttleConsoleAutopilotPositionMessage()
        {
            Coordinates = obj,
            Angle = angle,
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _window?.Dispose();
        }
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is ShuttleHullStateMessage hull)
            _window?.UpdateHullState(hull.State);
        else if (message is ShieldReadoutMessage shield && _window is { } window)
        {
            window.CombatPanel.UpdateShieldState(shield.Readout);
            window.UpdateShieldState(shield.Readout);
        }
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not ShuttleBoundUserInterfaceState cState)
            return;

        _window?.UpdateState(Owner, cState);
        _window?.UpdateShieldState(cState.ShieldState);
        _window?.UpdateHullState(cState.HullState);
        UpdateSelectedWeapons();
    }
}
