using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Bitrunning;

[Serializable, NetSerializable]
public sealed partial class BitrunMinebotRepairEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class BitrunBeaconRetrieveEvent : SimpleDoAfterEvent;
