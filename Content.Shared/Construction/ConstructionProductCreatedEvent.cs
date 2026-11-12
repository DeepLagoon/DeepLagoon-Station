namespace Content.Shared.Construction;

[ByRefEvent]
public readonly record struct ConstructionProductCreatedEvent(EntityUid Product);
