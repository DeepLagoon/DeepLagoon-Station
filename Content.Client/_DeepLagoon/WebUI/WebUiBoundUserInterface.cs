using Content.Client.UserInterface.Controls;
using System.Numerics;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.WebUI;

public sealed class WebUiWindow : FancyWindow
{
    public override void Close() { if (Panel.CanCloseNative()) base.Close(); }
    public readonly TguiPanel Panel = new();
    public GameWebView Web => Panel.Web;
    public WebUiWindow()
    {
        Title = "TGUI";
        SetSize = new Vector2(650, 500);
        MinSize = new Vector2(300, 200);
        ContentsContainer.AddChild(Panel);
    }
}

public sealed class WebUiBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private WebUiWindow? _window;
    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<WebUiWindow>();
        _window.Panel.OnClose += Close;
        _window.Panel.OnAction += (action, payload) => SendMessage(new WebUiActionMessage(action, payload));
        PushState();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        PushState();
    }

    private void PushState()
    {
        if (_window == null || State is not WebUiState state)
            return;
        _window.Panel.SetState(state.InterfaceName, state.DataJson);
    }
}
