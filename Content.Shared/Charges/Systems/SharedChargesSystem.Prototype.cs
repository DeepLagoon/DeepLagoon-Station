using Content.Shared.Charges.Components;

namespace Content.Shared.Charges.Systems;

public abstract partial class SharedChargesSystem
{
    /// <summary>Nominal charge yield of an ammunition prototype, without spawning a gameplay item.</summary>
    public int PrototypeAmmoCharges(LimitedChargesAmmoComponent ammo) => ammo.Charges;
}
