using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Mantis;

[Serializable, NetSerializable]
public enum NoosphericSignatureType : byte
{

    Sapient,

    Kinetic,

    Anomalous,
}

[Serializable, NetSerializable]
public enum NoosphericSector : byte
{
    Self,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    NorthWest,
}

[Serializable, NetSerializable]
public sealed class NoosphericSignature(
    NoosphericSignatureType type,
    NoosphericSector sector,
    float intensity,
    string name,
    Vector2 position)
{

    public readonly NoosphericSignatureType Type = type;

    public readonly NoosphericSector Sector = sector;

    public readonly float Intensity = intensity;

    public readonly string Name = name;

    public readonly Vector2 Position = position;
}
