using Content.Shared.StationRecords;

namespace Content.Server._DeepLagoon.Mantis;

[RegisterComponent]
public sealed partial class PsiRegistryCartridgeComponent : Component
{

    [ViewVariables]
    public StationRecordKey? SelectedKey;
}
