using Robust.Shared.Serialization;

namespace Content.Shared.Exodus.ShipShields;

[Serializable, NetSerializable]
public struct ShipShieldState
{
    public float BaseDraw;
    public float Draw;
    public float MaxDraw;
    public bool Recharging;
    public float OverloadAccumulator;
    public float HealthFraction;
    public bool Active;
    public bool Powered;
}
