using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Apartments;

[RegisterComponent]
public sealed partial class ApartmentAtmosphereComponent : Component
{
    public const int DefaultOxygenPercent = 21;
}

[Serializable, NetSerializable]
public enum ApartmentAtmosphereUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class ApartmentAtmosphereState(int oxygenPercent, bool active, bool saved = true) : BoundUserInterfaceState
{
    public readonly int OxygenPercent = oxygenPercent;
    public readonly bool Active = active;
    public readonly bool Saved = saved;
}

[Serializable, NetSerializable]
public sealed class ApartmentAtmosphereSetMessage(int oxygenPercent) : BoundUserInterfaceMessage
{
    public readonly int OxygenPercent = oxygenPercent;
}
