using Robust.Shared.GameStates;

namespace Content.Shared.Mobs.Components;

[RegisterComponent, NetworkedComponent, UnsavedComponent, AutoGenerateComponentState]
public sealed partial class CriticalCrawlingComponent : Component
{
    [AutoNetworkedField]
    public bool CanCrawl;

    // Retained for the whole critical episode, even if crawling becomes unavailable.
    public bool AdrenalineInjected;
}
