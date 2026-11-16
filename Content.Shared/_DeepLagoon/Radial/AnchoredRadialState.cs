using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._DeepLagoon.Radial;

[Serializable, NetSerializable]
public sealed record RadialChoice(int Id, string Name, SpriteSpecifier Icon, bool Selected);

[Serializable, NetSerializable]
public sealed class AnchoredRadialState(NetEntity anchor, NetEntity viewer, RadialChoice[] choices) : BoundUserInterfaceState
{
    public readonly NetEntity Anchor = anchor;
    public readonly NetEntity Viewer = viewer;
    public readonly RadialChoice[] Choices = choices;
}

[Serializable, NetSerializable]
public sealed class RadialSelection(int choice) : BoundUserInterfaceMessage
{
    public readonly int Choice = choice;
}
