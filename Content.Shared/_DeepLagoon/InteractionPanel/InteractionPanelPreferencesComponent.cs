using Content.Shared.Preferences;

namespace Content.Shared._DeepLagoon.InteractionPanel;

[RegisterComponent]
public sealed partial class InteractionPanelPreferencesComponent : Component
{
    public HumanoidCharacterProfile Profile = new();
}
