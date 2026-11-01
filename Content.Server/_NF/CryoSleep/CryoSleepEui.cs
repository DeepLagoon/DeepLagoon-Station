using Content.Server.EUI;
using Content.Shared._NF.CryoSleep;
using Content.Shared.Eui;

namespace Content.Server._NF.CryoSleep;

public sealed class CryoSleepEui : BaseEui
{
    private readonly CryoSleepSystem _cryoSystem;
    private readonly EntityUid _body;
    private readonly EntityUid _cryopod;

    public CryoSleepEui(EntityUid body, EntityUid cryopod, CryoSleepSystem cryoSys)
    {
        _body = body;
        _cryopod = cryopod;
        _cryoSystem = cryoSys;
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is not AcceptCryoChoiceMessage choice)
        {
            base.HandleMessage(msg);
            return;
        }

        if (_body is { Valid: true })
        {
            if (choice.Button == AcceptCryoUiButton.Accept)
            {
                // Exodus-Start
                //_cryoSystem.CryoStoreBody(_body, _cryopod);
                _cryoSystem.AcceptCryoSleep(_body, _cryopod);
                // Exodus-End
            }
            else
            {
                _cryoSystem.EjectBody(_cryopod, body: _body);
            }
        }

        if (!IsShutDown)
            Close();
    }

    public override void Closed()
    {
        _cryoSystem.OnConfirmationClosed(_body, _cryopod, this);
    }
}
