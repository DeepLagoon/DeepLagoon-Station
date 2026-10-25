using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Salvage.Expeditions;

[NetworkedComponent]
public abstract partial class SharedSalvageExpeditionComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite), DataField("stage")]
    public ExpeditionStage Stage = ExpeditionStage.Added;

    public int ObjectivesTotal;
    public int ObjectivesCompleted;
}

[Serializable, NetSerializable]
public sealed class SalvageExpeditionComponentState : ComponentState
{
    public ExpeditionStage Stage;
    public TimeSpan EndTime;
    public int ObjectivesTotal;
    public int ObjectivesCompleted;
}
