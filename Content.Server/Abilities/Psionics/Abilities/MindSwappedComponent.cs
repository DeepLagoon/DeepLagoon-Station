using Robust.Shared.Prototypes;

namespace Content.Server.Abilities.Psionics
{
    [RegisterComponent]
    public sealed partial class MindSwappedComponent : Component
    {
        [ViewVariables]
        public EntityUid OriginalEntity = default!;
        [DataField("mindSwapReturnActionId")]
        public EntProtoId? MindSwapReturnActionId = "ActionPsionicMindSwapReturn";

        [DataField("mindSwapReturnActionEntity")]
        public EntityUid? MindSwapReturnActionEntity;
    }
}
