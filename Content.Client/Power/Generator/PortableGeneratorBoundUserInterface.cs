using Content.Shared.Power.Generator;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client.Power.Generator;

[UsedImplicitly]
public sealed partial class PortableGeneratorBoundUserInterface : BoundUserInterface
{
    private GeneratorWindow? _window;

    public PortableGeneratorBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        if (OpenTgui())
            return;
        OpenClassic();
    }

    private void OpenClassic()
    {
        // The engine registers one native control per BUI until the BUI closes.
        // Switching presentation must reuse it, including its event subscriptions.
        if (_window != null)
        {
            _window.Open(_window.Position);
            if (_latestState != null) _window.Update(_latestState);
            return;
        }

        _window = this.CreateWindowCenteredLeft<GeneratorWindow>();
        Content.Client._DeepLagoon.WebUI.WindowInterfaceStyle.Attach(_window, "PortableGenerator", () => false, next =>
        {
            Content.Client._DeepLagoon.WebUI.WindowInterfaceStyle.Save("PortableGenerator", next);
            SwitchInterface(next);
        });
        _window.SetEntity(Owner);
        _window.OnState += args =>
        {
            if (args)
            {
                Start();
            }
            else
            {
                Stop();
            }
        };

        _window.OnPower += SetTargetPower;
        _window.OnEjectFuel += EjectFuel;
        _window.OnSwitchOutput += SwitchOutput;
        if (_latestState != null)
            _window.Update(_latestState);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not PortableGeneratorComponentBuiState msg)
            return;

        _latestState = msg;
        _window?.Update(msg);
        PublishTgui();
    }

    public void SetTargetPower(int target)
    {
        SendMessage(new PortableGeneratorSetTargetPowerMessage(target));
    }

    public void Start()
    {
        SendMessage(new PortableGeneratorStartMessage());
    }

    public void Stop()
    {
        SendMessage(new PortableGeneratorStopMessage());
    }

    public void SwitchOutput()
    {
        SendMessage(new PortableGeneratorSwitchOutputMessage());
    }

    public void EjectFuel()
    {
        SendMessage(new PortableGeneratorEjectFuelMessage());
    }
}
