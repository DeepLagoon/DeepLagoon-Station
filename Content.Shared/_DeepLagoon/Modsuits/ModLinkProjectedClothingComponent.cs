using Content.Shared.Clothing.Components;
using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Modsuits;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class ModLinkProjectedClothingComponent : Component
{
    [AutoNetworkedField]
    public ChameleonClothingAppearance? Appearance;

    [AutoNetworkedField]
    public string? Slot;

    [AutoNetworkedField]
    public string? Species;
}
