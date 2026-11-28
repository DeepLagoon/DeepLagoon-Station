using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Psionics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Mantis;

public sealed class PsiRegistryTguiView : BoxContainer
{
    private readonly TguiPanel _panel;
    private PsiRegistryUiState? _state;
    private bool _readyRetryPending;
    private float _readyWait;

    public event Action<uint?>? OnSelect;

    public event Action<PsionicsStatus, string?>? OnChangeStatus;

    public PsiRegistryTguiView(string styleKey)
    {
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;

        _panel = new TguiPanel(suspendWhenHidden: false, inheritChatAppearance: false,
            showWindowControls: false)
        {
            InterfaceStyleKey = styleKey,
        };
        _panel.OnAction += OnAction;
        _panel.SetStaticData(new TguiData().Object("labels", PsiRegistryTguiData.Labels()).ToString());
        AddChild(_panel);
    }

    public void UpdateState(PsiRegistryUiState state)
    {
        _state = state;
        _panel.SetIncrementalState("PsiRegistry", PsiRegistryTguiData.Build(state));
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (_readyRetryPending || !_panel.Web.IsReady)
        {
            if (!_panel.Web.IsReady)
            {
                if ((_readyWait += args.DeltaSeconds) > 8f && !_readyRetryPending)
                {
                    _readyRetryPending = true;
                    if (_state is { } state)
                        UpdateState(state);
                }
                return;
            }

            _readyRetryPending = false;
            _readyWait = 0;
            if (_state is { } latest)
                UpdateState(latest);
        }
    }

    private void OnAction(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var args) || args == null)
            return;

        switch (action)
        {
            case "select":
                if (args.TryInt("key", out var key))
                    OnSelect?.Invoke((uint) key);
                break;

            case "status":
                if (args.String("value") is { } value && Enum.TryParse<PsionicsStatus>(value, true, out var status))
                    OnChangeStatus?.Invoke(status, status == PsionicsStatus.None ? null : args.String("reason"));
                break;
        }
    }
}
