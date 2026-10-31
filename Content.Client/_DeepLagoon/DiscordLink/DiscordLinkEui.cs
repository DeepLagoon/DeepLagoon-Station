using Content.Client.Eui;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Eui;
using JetBrains.Annotations;
using Robust.Client;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Input;
using Robust.Shared.Maths;

namespace Content.Client._DeepLagoon.DiscordLink;

[UsedImplicitly]
public sealed partial class DiscordLinkEui : BaseEui
{
    private const string DiscordInvite = "https://discord.gg/uW7tgZeqVg";
    private const string DiscordLinkText = "Откройте канал привязки Discord";
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
    private readonly BoxContainer _linkLine = new() { SeparationOverride = 0, Visible = false };
    private readonly Label _linkSuffix = new();
    private readonly LineEdit _code = new() { Editable = false };
    private readonly Button _generate = new() { Text = "Создать код" };
    private readonly Button _existing = new() { Text = "У меня уже есть аккаунт" };
    [Dependency] private IClipboardManager _clipboard = default!;
    [Dependency] private IBaseClient _client = default!;
    private readonly Button _copy = new() { Text = "Копировать код", Disabled = true };
    private bool _serverClosed;

    public DiscordLinkEui()
    {
        var discordLink = new Label
        {
            Text = DiscordLinkText,
            FontColorOverride = Color.LightBlue,
            MouseFilter = Control.MouseFilterMode.Stop,
            DefaultCursorShape = Control.CursorShape.Hand,
            ToolTip = DiscordInvite,
        };
        discordLink.OnMouseEntered += _ => discordLink.FontColorOverride = Color.Cyan;
        discordLink.OnMouseExited += _ => discordLink.FontColorOverride = Color.LightBlue;
        discordLink.OnKeyBindDown += args =>
        {
            if (args.Function == EngineKeyFunctions.UIClick)
                OpenDiscord();
        };
        _linkLine.AddChild(discordLink);
        _linkLine.AddChild(_linkSuffix);
        var instructions = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 0,
            Children = { _linkLine, _message },
        };
        var discord = new Button { Text = "Перейти в дискорд", ToolTip = DiscordInvite };
        discord.OnPressed += _ => OpenDiscord();
        var check = new Button { Text = "Проверить привязку" };
        var disconnect = new Button { Text = "Отключиться от сервера" };
        disconnect.OnPressed += _ => _client.DisconnectFromServer("Проверка доступа отменена игроком");
        _window.Contents.AddChild(new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Children = { instructions, discord, _code, _copy, _generate, _existing, check, disconnect },
        });
        _copy.OnPressed += _ =>
        {
            _clipboard.SetText(_code.Text);
            _copy.Text = "Код скопирован";
        };
        _generate.OnPressed += _ => SendMessage(new GenerateDiscordLinkCode());
        _existing.OnPressed += _ => SendMessage(new GenerateDiscordAccountMergeCode());
        check.OnPressed += _ => SendMessage(new CheckDiscordLink());
        _window.OnClose += () =>
        {
            if (!_serverClosed)
                SendMessage(new CloseEuiMessage());
        };
    }

    private static void OpenDiscord() => IoCManager.Resolve<IUriOpener>().OpenUri(DiscordInvite);

    public override void Opened() => _window.OpenCentered();
    public override void Closed()
    {
        _serverClosed = true;
        _window.ServerClosed = true;
        _window.Close();
        _window?.Dispose();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not DiscordLinkEuiState s)
            return;
        _linkLine.Visible = s.Message.StartsWith(DiscordLinkText, StringComparison.Ordinal);
        if (_linkLine.Visible)
        {
            var lineEnd = s.Message.IndexOf('\n');
            if (lineEnd < 0)
                lineEnd = s.Message.Length;
            _linkSuffix.Text = s.Message[DiscordLinkText.Length..lineEnd];
            _message.Text = lineEnd < s.Message.Length ? s.Message[(lineEnd + 1)..] : "";
        }
        else
            _message.Text = s.Message;
        _message.Visible = !string.IsNullOrEmpty(_message.Text);
        if (_code.Text != s.Code)
            _copy.Text = "Копировать код";
        _code.Text = s.Code;
        _copy.Disabled = string.IsNullOrEmpty(s.Code);
        _generate.Disabled = s.Linked;
        _existing.Disabled = s.Linked;
    }
}
