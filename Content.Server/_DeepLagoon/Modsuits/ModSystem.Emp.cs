using Content.Shared.PowerCell;

namespace Content.Server._DeepLagoon.Modsuits;

/// <summary>
/// Protects a powered suit's own cell while the EMP shield module is active.
/// </summary>
[RegisterComponent]
public sealed partial class ModEmpShieldComponent : Component;

public sealed partial class ModSystem
{
    private void InitializeEmp()
    {
        SubscribeLocalEvent<ModEmpShieldComponent, PowerCellEmpAttemptEvent>(OnModCellEmpAttempt);
    }

    private void OnModCellEmpAttempt(Entity<ModEmpShieldComponent> suit, ref PowerCellEmpAttemptEvent args)
    {
        if (args.Cancelled ||
            !TryComp<ModControlComponent>(suit, out var control) ||
            !IsSealed(suit) ||
            !_power.TryGetBatteryFromSlot(suit, out var installedCell, out _) ||
            installedCell != args.Cell)
            return;

        var cost = 10 + 5 * Complexity((suit.Owner, control));
        if (_power.TryUseCharge(suit, cost))
            args.Cancelled = true;
    }
}
