using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Economy;

[Serializable, NetSerializable]
public sealed partial class MaintenanceFinishedEvent : SimpleDoAfterEvent;
