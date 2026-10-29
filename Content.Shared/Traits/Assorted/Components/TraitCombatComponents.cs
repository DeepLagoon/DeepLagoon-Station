using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Traits.Assorted.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TraitArmorComponent : Component
{
    [DataField, AutoNetworkedField] public HashSet<ProtoId<DamageModifierSetPrototype>> ModifierSets = new();
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TraitMartialArtistComponent : Component
{
    [DataField, AutoNetworkedField] public DamageModifierSet Modifiers = new();
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class OniDamageModifierComponent : Component
{
    [DataField, AutoNetworkedField] public DamageModifierSet Modifiers = new();
}
