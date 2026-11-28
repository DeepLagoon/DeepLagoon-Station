using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.BodyInsurance;

[Serializable, NetSerializable]
public sealed partial class BodyInsuranceReturnDoAfterEvent
    : SimpleDoAfterEvent
{
}
