using Content.Shared._DeepLagoon.BodyInsurance;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;

namespace Content.Server._DeepLagoon.BodyInsurance;

public sealed partial class BodyInsuranceSystem
{
    [Dependency]
    private AppearanceSystem _machineAppearance = default!;

    private readonly HashSet<EntityUid> _bitingMachines = new();

    private void PlayMachineBite(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid))
            return;

        if (!HasComp<AppearanceComponent>(uid))
            return;

        // Не запускаем несколько анимаций одновременно.
        if (!_bitingMachines.Add(uid))
            return;

        _machineAppearance.SetData(
            uid,
            BodyInsuranceMachineVisuals.Biting,
            true);

        // Возвращаем исходный спрайт через 640 мс.
        Robust.Shared.Timing.Timer.Spawn(640, () =>
        {
            _bitingMachines.Remove(uid);

            if (TerminatingOrDeleted(uid))
                return;

            _machineAppearance.SetData(
                uid,
                BodyInsuranceMachineVisuals.Biting,
                false);
        });
    }
}
