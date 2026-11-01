using Content.Client.Eui;
using Content.Shared._NF.CryoSleep;
using JetBrains.Annotations;
using Robust.Client.Graphics;

namespace Content.Client._NF.CryoSleep;

[UsedImplicitly]
public sealed class CryoSleepEui : BaseEui
{
    private readonly AcceptCryoWindow _window;
    private bool _choiceSent;

    public CryoSleepEui()
    {
        _window = new AcceptCryoWindow();

        _window.DenyButton.OnPressed += _ => Choose(AcceptCryoUiButton.Deny);
        _window.AcceptButton.OnPressed += _ => Choose(AcceptCryoUiButton.Accept);
        _window.OnClose += () => Choose(AcceptCryoUiButton.Deny);
    }

    private void Choose(AcceptCryoUiButton choice)
    {
        if (_choiceSent)
            return;

        _choiceSent = true;
        SendMessage(new AcceptCryoChoiceMessage(choice));
        _window.Close();
    }

    public override void Opened()
    {
        IoCManager.Resolve<IClyde>().RequestWindowAttention();
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _choiceSent = true;
        _window.Close();
    }

}
