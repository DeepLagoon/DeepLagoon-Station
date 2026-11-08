using Content.Client.Eui;
using Content.Shared.Eui;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._DeepLagoon.ShuttleParking;

public sealed class AutoParkingNoticeEui : BaseEui
{
    private readonly DefaultWindow _window;

    public AutoParkingNoticeEui()
    {
        _window = new DefaultWindow { Title = Loc.GetString("shuttle-auto-parking-title"), MinWidth = 360 };
        var content = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(8), SeparationOverride = 12 };
        var message = new RichTextLabel { MaxWidth = 400 };
        message.SetMessage(Loc.GetString("shuttle-auto-parking-message"));
        content.AddChild(message);

        var ok = new Button { Text = Loc.GetString("quick-dialog-ui-ok"), HorizontalAlignment = Control.HAlignment.Right };
        ok.OnPressed += _ => _window.Close();
        content.AddChild(ok);
        _window.Contents.AddChild(content);
        _window.OnClose += OnWindowClosed;
    }

    public override void Opened() => _window.OpenCentered();

    public override void Closed()
    {
        _window.OnClose -= OnWindowClosed;
        _window.Close();
    }

    private void OnWindowClosed() => SendMessage(new CloseEuiMessage());
}
