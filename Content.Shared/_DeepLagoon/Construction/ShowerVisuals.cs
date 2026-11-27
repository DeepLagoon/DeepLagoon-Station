using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Construction;

[Serializable, NetSerializable]
public enum ShowerVisuals : byte
{
    Water,
    Mist,
}

[Serializable, NetSerializable]
public enum ShowerVisualLayers : byte
{
    Base,
    Water,
    Mist,
}
