using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.BUIStates;

/// <summary>
/// A bounded, abstract ship schematic. No sprites, entity identities or damage values are transmitted.
/// Cells are row-major with increasing grid Y; bits: 1 footprint, 2 missing, 4 engine, 8 gun, 16 structure.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShuttleHullState
{
    public int Width;
    public int Height;
    public byte[] Cells = Array.Empty<byte>();
    public int ExpectedThrusters;
    public int MissingThrusters;
    public int ExpectedWeapons;
    public int MissingWeapons;
    public int ExpectedStructures;
    public int MissingStructures;
    public int ExpectedTiles;
    public int MissingTiles;
    public float IntegrityFraction = 1f;
    public int WorkingThrusters;
    public int WorkingGyroscopes;
    public float StoredEnergy;
    public float EnergyCapacity;
}

[Serializable, NetSerializable]
public sealed class ShuttleHullStateMessage(ShuttleHullState? state) : BoundUserInterfaceMessage
{
    public ShuttleHullState? State = state;
}
