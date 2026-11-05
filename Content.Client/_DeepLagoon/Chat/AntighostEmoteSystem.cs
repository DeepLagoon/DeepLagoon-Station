using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Chat;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;

namespace Content.Client._DeepLagoon.Chat;

[CVarDefs]
public sealed class AntighostEmoteCVars
{
    public static readonly CVarDef<int> Audience = CVarDef.Create("chat.antighost_audience",
        0, CVar.CLIENTONLY | CVar.ARCHIVE);
}

public sealed class AntighostEmoteSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    private FancyWindow? _window;

    public override void Initialize()
    {
        SubscribeNetworkEvent<AntighostEmotePromptEvent>(Open);
    }

    public override void Shutdown()
    {
        _window?.Close();
        base.Shutdown();
    }

    private void Open(AntighostEmotePromptEvent prompt)
    {
        _window?.Close();
        var window = new FancyWindow { Title = Loc.GetString("dl-antighost-title"), MinWidth = 450 };
        _window = window;
        var body = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        window.ContentsContainer.AddChild(body);
        var recipient = new Label { Text = Loc.GetString("dl-antighost-recipient", ("name", prompt.Recipient)) };
        var text = new LineEdit { PlaceHolder = Loc.GetString("dl-antighost-placeholder"), HorizontalExpand = true };
        var next = new Button { Text = Loc.GetString("dl-antighost-next"), Disabled = true };
        var ask = new CheckBox
        {
            Text = Loc.GetString("dl-antighost-ask-again"),
            Visible = _cfg.GetCVar(AntighostEmoteCVars.Audience) is 1 or 2,
        };
        UpdateButton();
        ask.OnToggled += _ => UpdateButton();
        text.OnTextChanged += _ => UpdateButton();
        text.OnTextEntered += _ => Continue();
        next.OnPressed += _ => Continue();
        body.AddChild(recipient);
        body.AddChild(text);
        body.AddChild(ask);
        body.AddChild(next);
        window.OnClose += () =>
        {
            if (_window == window)
                _window = null;
            window.Dispose();
        };
        window.OpenCentered();
        text.GrabKeyboardFocus();

        void UpdateButton()
        {
            next.Disabled = !ValidText(text.Text);
            var saved = ask.Pressed ? 0 : _cfg.GetCVar(AntighostEmoteCVars.Audience);
            next.Text = Loc.GetString(saved switch
            {
                1 => "dl-antighost-send-private",
                2 => "dl-antighost-send-nearby",
                _ => "dl-antighost-next",
            });
        }

        void Continue()
        {
            var draft = text.Text.Trim();
            if (!ValidText(draft))
                return;

            var saved = ask.Pressed ? 0 : _cfg.GetCVar(AntighostEmoteCVars.Audience);
            if (saved is 1 or 2)
            {
                Send(draft, saved == 2);
                return;
            }

            body.RemoveAllChildren();
            body.AddChild(new Label { Text = Loc.GetString("dl-antighost-audience") });
            var remember = new CheckBox { Text = Loc.GetString("dl-antighost-remember") };
            body.AddChild(remember);
            var buttons = new BoxContainer { SeparationOverride = 8 };
            body.AddChild(buttons);
            foreach (var (nearby, label) in new[] { (false, "dl-antighost-private"), (true, "dl-antighost-nearby") })
            {
                var button = new Button { Text = Loc.GetString(label), HorizontalExpand = true };
                button.OnPressed += _ =>
                {
                    if (remember.Pressed || ask.Pressed)
                    {
                        _cfg.SetCVar(AntighostEmoteCVars.Audience, remember.Pressed ? nearby ? 2 : 1 : 0);
                        _cfg.SaveToFile();
                    }
                    Send(draft, nearby);
                };
                buttons.AddChild(button);
            }
        }

        void Send(string draft, bool nearby)
        {
            RaiseNetworkEvent(new AntighostEmoteSendEvent(prompt.RequestId, draft, nearby));
            window.Close();
        }
    }

    private static bool ValidText(string text) => text.Length <= AntighostEmote.MaxLength && !string.IsNullOrWhiteSpace(text);
}
