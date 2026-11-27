using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Fragments;
using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Mantis;

public sealed partial class PsiRegistryUi : UIFragment
{
    private const string StyleKey = "PsiRegistry";

    private PsiRegistryHost? _host;
    private PsiRegistryUiFragment? _fragment;
    private PsiRegistryTguiView? _tguiView;
    private BoundUserInterface? _userInterface;
    private PsiRegistryUiState? _state;

    public override Control GetUIFragmentRoot()
    {

        return _host ??= BuildHost();
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _userInterface = userInterface;
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not PsiRegistryUiState registryState)
            return;

        _state = registryState;
        _fragment?.UpdateState(registryState);
        _tguiView?.UpdateState(registryState);
    }

    private PsiRegistryHost BuildHost()
    {
        var host = new PsiRegistryHost();

        _fragment = new PsiRegistryUiFragment();
        _fragment.OnRecordSelected += key => Send(new PsiRegistrySelectMessage(key));
        _fragment.OnStatusRequested += (status, reason) =>
            Send(new PsiRegistryChangeStatusMessage(status, reason));
        host.AddPresentation(_fragment);

        _tguiView = new PsiRegistryTguiView(StyleKey);
        _tguiView.OnSelect += key => Send(new PsiRegistrySelectMessage(key));
        _tguiView.OnChangeStatus += (status, reason) =>
            Send(new PsiRegistryChangeStatusMessage(status, reason));
        host.AddPresentation(_tguiView);

        WindowInterfaceStyle.Attach(host.ButtonRow, StyleKey,
            () => WindowInterfaceStyle.UseTgui(StyleKey),
            next =>
            {
                WindowInterfaceStyle.Save(StyleKey, next);
                ApplyStyle();
            });

        ApplyStyle();
        return host;
    }

    private void ApplyStyle()
    {
        var tgui = WindowInterfaceStyle.UseTgui(StyleKey);

        if (_fragment != null)
            _fragment.Visible = !tgui;

        if (_tguiView != null)
        {
            _tguiView.Visible = tgui;

            if (tgui && _state is { } state)
                _tguiView.UpdateState(state);
        }
    }

    private void Send(CartridgeMessageEvent messageEvent)
    {
        _userInterface?.SendMessage(new CartridgeUiMessage(messageEvent));
    }
}

public sealed class PsiRegistryHost : BoxContainer
{

    public readonly BoxContainer ButtonRow;

    public readonly LayoutContainer Content;

    public PsiRegistryHost()
    {
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;

        ButtonRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        AddChild(ButtonRow);

        Content = new LayoutContainer { VerticalExpand = true, HorizontalExpand = true };
        AddChild(Content);
    }

    public void AddPresentation(Control control)
    {
        Content.AddChild(control);
        LayoutContainer.SetAnchorPreset(control, LayoutContainer.LayoutPreset.Wide);
    }
}
