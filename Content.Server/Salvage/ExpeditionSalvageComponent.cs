namespace Content.Server.Salvage;

/// <summary>
/// Permanent, serialized provenance of an expedition defender or its anatomy.
/// Unlike an entity reference, this survives dismemberment, transplantation and parking.
/// Equipment carried by the defender is deliberately not tagged.
/// </summary>
[RegisterComponent]
public sealed partial class ExpeditionSalvageComponent : Component;

/// <summary>A kitchen spike's pending butcher products inherit the victim's export restriction.</summary>
[RegisterComponent]
public sealed partial class ExpeditionButcherSourceComponent : Component;
