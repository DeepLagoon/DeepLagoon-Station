using Content.Server._Mono.Radar;
using Content.Server.Medical.SuitSensors;
using Content.Shared.Inventory;
using Content.Shared.Roles;

namespace Content.Server.Jobs;

public sealed partial class RemoveStartingSuitSensorsSpecial : JobSpecial
{
    public override void AfterEquip(EntityUid mob)
    {
        var entities = IoCManager.Resolve<IEntityManager>();
        if (!entities.System<InventorySystem>().TryGetSlotEntity(mob, "jumpsuit", out var uniform))
            return;

        entities.RemoveComponent<SuitSensorComponent>(uniform.Value);
        entities.RemoveComponent<ToggleableSignatureComponent>(uniform.Value);
    }
}
