using Robust.Shared.Serialization;
using Content.Shared.DoAfter;

namespace Content.Shared._DeepLagoon.LustWish;

[RegisterComponent]
public sealed partial class BlueMoonProtectionComponent : Component
{
    [DataField] public bool CaptureFluids;
    [DataField] public bool Chastity;
    [DataField] public bool Wrapped;
}

[Serializable, NetSerializable]
public sealed partial class BlueMoonProtectionAttachEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public enum BlueMoonProtectionVisuals : byte { State }
