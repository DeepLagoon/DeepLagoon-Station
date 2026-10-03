using Content.Client.Eui;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Eui;
using JetBrains.Annotations;
using Robust.Client;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._DeepLagoon.DiscordLink;

[UsedImplicitly]
public sealed class DiscordLinkEui : BaseEui
{
    private sealed class AdmissionWindow : DefaultWindow
    {
        public AdmissionWindow() => CloseButton.Visible = false;
        public bool ServerClosed;
        public override void Close()
        {
            if (ServerClosed)
                base.Close();
        }
    }
    private readonly AdmissionWindow _window = new() { Title = "Привязка Discord" };
    private readonly Label _message = new();
    private readonly LineEdit _code = new() { Editable = false };
    private readonly Button _generate = new() { Text = "Создать код" };
    [Dependency] private readonly IClipboardManager _clipboard = default!;
    [Dependency] private readonly IBaseClient _client = default!;
    private readonly Button _copy = new() { Text = "Копировать код", Disabled = true };
    private bool _serverClosed;

    public DiscordLinkEui()
    {
        var check = new Button { Text = "Проверить привязку" };
        var disconnect = new Button { Text = "Отключиться от сервера" };
        disconnect.OnPressed += _ => _client.DisconnectFromServer("Проверка доступа отменена игроком");
        _window.Contents.AddChild(new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Children = { _message, _code, _copy, _generate, check, disconnect },
        });
        _copy.OnPressed += _ =>
        {
            _clipboard.SetText(_code.Text);
            _copy.Text = "Код скопирован";
        };
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
        _window.ServerClosed = true;
        _window.Close();
        _window.Dispose();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not DiscordLinkEuiState s)
            return;
        _message.Text = s.Message;
        if (_code.Text != s.Code)
            _copy.Text = "Копировать код";
        _code.Text = s.Code;
        _copy.Disabled = string.IsNullOrEmpty(s.Code);
        _generate.Disabled = s.Linked;
    }
}
