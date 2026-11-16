using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.Modsuits;

[RegisterComponent]
public sealed partial class ModStartingModulesComponent : Component
{
    [DataField(required: true)]
    public List<EntProtoId> Modules = new();
}
