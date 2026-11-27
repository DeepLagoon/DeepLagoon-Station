using Content.Server.Power.Components;
using Content.Shared.Materials;

namespace Content.Server.Materials;

public sealed partial class MaterialReclaimerSystem
{
    internal void RestoreAfterLoad(EntityUid uid, MaterialReclaimerComponent reclaimer)
    {
        if (TryComp<ActiveMaterialReclaimerComponent>(uid, out var active) &&
            active.ReclaimingContainer.ContainedEntities.Count == 0)
            RemComp<ActiveMaterialReclaimerComponent>(uid);

        if (TryComp<ApcPowerReceiverComponent>(uid, out var power))
            reclaimer.Powered = power.Powered;
        _appearance.SetData(uid, RecyclerVisuals.Broken, reclaimer.Broken);
        SetReclaimerEnabled(uid, reclaimer.Enabled, reclaimer);
    }
}
