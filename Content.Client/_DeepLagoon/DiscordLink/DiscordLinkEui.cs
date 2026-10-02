using Content.Client.Eui;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Eui;
using JetBrains.Annotations;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._DeepLagoon.DiscordLink;

[UsedImplicitly]
public sealed class DiscordLinkEui : BaseEui
{
    private readonly DefaultWindow _window = new() { Title = "Привязка Discord" };
    private readonly Label _message = new();
    private readonly LineEdit _code = new() { Editable = false };
    private readonly Button _generate = new() { Text = "Создать код" };
    private bool _serverClosed;

    public DiscordLinkEui()
    {
        var check = new Button { Text = "Проверить привязку" };
        _window.Contents.AddChild(new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Children = { _message, _code, _generate, check },
        });
        _generate.OnPressed += _ => SendMessage(new GenerateDiscordLinkCode());
        check.OnPressed += _ => SendMessage(new CheckDiscordLink());
        _window.OnClose += () =>
        {
            if (!_serverClosed)
                SendMessage(new CloseEuiMessage());
        };
    }

    public override void Opened() => _window.OpenCentered();
    public override void Closed()
    {
        _serverClosed = true;
        _window.Close();
        _window.Dispose();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not DiscordLinkEuiState s)
            return;
        _message.Text = s.Message;
        _code.Text = s.Code;
        _generate.Disabled = s.Linked;
    }
}
