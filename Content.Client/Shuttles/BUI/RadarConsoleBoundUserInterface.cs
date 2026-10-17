using Content.Client.Shuttles.UI;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Item;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using RadarConsoleWindow = Content.Client.Shuttles.UI.RadarConsoleWindow;

namespace Content.Client.Shuttles.BUI;

[UsedImplicitly]
public sealed class RadarConsoleBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private RadarConsoleWindow? _window;

    public RadarConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        // Apply the handheld size before the window is positioned on screen.
        _window = this.CreateDisposableControl<RadarConsoleWindow>();
        _window.SetHandheld(EntMan.HasComponent<ItemComponent>(Owner));
        _window.SetConsole(Owner);
        _window.OnClose += Close;
        var ui = EntMan.System<UserInterfaceSystem>();
        ui.RegisterControl(this, _window);
        if (ui.TryGetPosition(Owner, UiKey, out var position))
            _window.Open(position);
        else
            _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not NavBoundUserInterfaceState cState)
            return;

        _window?.UpdateState(cState.State);
    }
}
