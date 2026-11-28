using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Construction;

[Serializable, NetSerializable]
public sealed partial class WallAttachmentToolDoAfterEvent : DoAfterEvent
{
    [DataField]
    public bool Anchor;

    public WallAttachmentToolDoAfterEvent(bool anchor) => Anchor = anchor;

    public override DoAfterEvent Clone() => this;
}
