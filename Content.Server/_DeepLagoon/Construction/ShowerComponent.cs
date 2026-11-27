using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._DeepLagoon.Construction;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class ShowerComponent : Component
{
    [DataField]
    public bool Running;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan StartedAt;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextSpill;

    [DataField]
    public TimeSpan MistDelay = TimeSpan.FromSeconds(5);

    [DataField]
    public TimeSpan SpillInterval = TimeSpan.FromSeconds(2);

    [ViewVariables]
    public bool Mist;
}
