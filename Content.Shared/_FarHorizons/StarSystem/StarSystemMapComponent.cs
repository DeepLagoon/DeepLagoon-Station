using Content.Shared._FarHorizons.StarSystem.Helpers;
using Content.Shared._FarHorizons.StarSystem.Prototypes;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._FarHorizons.StarSystem;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class StarSystemMapComponent : Component
{
    [DataField, ViewVariables, AutoNetworkedField] public ProtoId<StarSystemPrototype>? System;
    [DataField, AutoNetworkedField] public SectorDefinition? Definition;
    [ViewVariables] public PlanetarySystem? StarSystem;
}
