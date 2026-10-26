using System.Numerics;
using Content.Client.Gameplay;
using Content.Client.Lobby;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Native onboarding, including connections that skip the lobby.</summary>
public sealed partial class InterfaceStyleUIController : UIController,
    IOnStateEntered<LobbyState>, IOnStateEntered<GameplayState>,
    IOnStateExited<LobbyState>, IOnStateExited<GameplayState>
{
    [Dependency] private IConfigurationManager _cfg = default!;
    private FancyWindow? _window;

    public void OnStateEntered(LobbyState state) => Ask();
    public void OnStateEntered(GameplayState state) => Ask();
    public void OnStateExited(LobbyState state) => Close();
    public void OnStateExited(GameplayState state) => Close();

    private void Ask()
    {
        if (_window != null || WebUiCVars.HasChosenStyle(_cfg.GetCVar(WebUiCVars.InterfaceStyle))) return;
        var window = new FancyWindow { Title = Loc.GetString("dl-ui-style-title"), SetSize = new Vector2(600, 360) };
        _window = window;
        var contents = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 12 };
        contents.AddChild(new Label { Text = Loc.GetString("dl-ui-style-question") });
        var hint = new RichTextLabel();
        hint.SetMessage(Loc.GetString("dl-ui-style-hint"));
        contents.AddChild(hint);
        foreach (var (style, label) in new[] { (1, "dl-ui-style-tgui"), (2, "dl-ui-style-classic") })
        {
            var button = new Button { Text = Loc.GetString(label) };
            button.OnPressed += _ =>
            {
                _cfg.SetCVar(WebUiCVars.InterfaceStyle, style);
                _cfg.SetCVar(WebUiCVars.VanillaChat, !WebUiCVars.UseTgui(style));
                _cfg.SaveToFile();
                contents.RemoveAllChildren();
                var confirmation = new RichTextLabel();
                confirmation.SetMessage(Loc.GetString("dl-ui-style-selected", ("style", Loc.GetString(label))));
                contents.AddChild(confirmation);
                var ok = new Button { Text = Loc.GetString("dl-ui-style-ok") };
                ok.OnPressed += _ => window.Close();
                contents.AddChild(ok);
            };
            contents.AddChild(button);
        }
        window.ContentsContainer.AddChild(contents);
        window.OnClose += () => { if (_window == window) _window = null; window.Dispose(); };
        window.OpenCentered();
    }

    private void Close() { _window?.Close(); _window = null; }
}
