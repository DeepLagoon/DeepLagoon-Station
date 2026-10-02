using Content.Shared.Preferences;

namespace Content.Server._DeepLagoon.InteractionPanel;

[RegisterComponent]
public sealed partial class InteractionPanelPreferencesComponent : Component
{
    public HumanoidCharacterProfile Profile = new();
}
