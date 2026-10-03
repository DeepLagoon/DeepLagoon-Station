using Robust.Client;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.DiscordLink;

public sealed class DiscordAdmissionState : Robust.Client.State.State
{
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly IBaseClient _client = default!;
    private BoxContainer? _screen;

    protected override void Startup()
    {
        _screen = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalAlignment = Control.HAlignment.Center,
            VerticalAlignment = Control.VAlignment.Center,
        };
        _screen.AddChild(new Label { Text = "Обязательная проверка доступа: сначала Discord, затем whitelist." });
        _screen.AddChild(new Label { Text = "Завершите привязку в открытом окне и дождитесь одобрения WL-заявки." });
        var disconnect = new Button { Text = "Отключиться от сервера" };
        disconnect.OnPressed += _ => _client.DisconnectFromServer("Проверка доступа отменена игроком");
        _screen.AddChild(disconnect);
        _ui.StateRoot.AddChild(_screen);
        LayoutContainer.SetAnchorPreset(_screen, LayoutContainer.LayoutPreset.Wide);
    }

    protected override void Shutdown()
    {
        _screen?.Dispose();
        _screen = null;
    }
}
