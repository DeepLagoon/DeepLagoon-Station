using Content.Server.EUI;
using Content.Shared.Cloning;
using Content.Shared.Eui;

namespace Content.Server.Cloning
{
    public sealed class AcceptCloningEui : BaseEui
    {
        private readonly EntityUid _mindId;
        private readonly EntityUid _clone;
        private readonly CloningSystem _cloningSystem;

        public AcceptCloningEui(EntityUid mindId, EntityUid clone, CloningSystem cloningSystem)
        {
            _mindId = mindId;
            _clone = clone;
            _cloningSystem = cloningSystem;
        }

        public override void HandleMessage(EuiMessageBase msg)
        {
            if (msg is AcceptCloningChoiceMessage { Button: AcceptCloningUiButton.Accept })
                _cloningSystem.TransferMindToClone(_mindId, _clone, Player.UserId);

            Close();
        }
    }
}
