using Content.Server.Spawners.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;

namespace Content.Server.Spawners.EntitySystems;

public sealed class SpawnOnDespawnSystem : EntitySystem
{
    [Dependency] private Content.Server._DeepLagoon.Salvage.ExpeditionLootSystem _expeditionLoot = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SpawnOnDespawnComponent, TimedDespawnEvent>(OnDespawn);
    }

    private void OnDespawn(EntityUid uid, SpawnOnDespawnComponent comp, ref TimedDespawnEvent args)
    {
        if (!TryComp(uid, out TransformComponent? xform))
            return;

        // Mono start - multiple entity spawning
            for (int i = 0; i <= comp.Count; i++)
            {
                _expeditionLoot.SpawnLoot(uid, comp.Prototype, xform.Coordinates);
            }
        // End mono
    }
    public void SetPrototype(Entity<SpawnOnDespawnComponent> entity, EntProtoId prototype)
    {
        entity.Comp.Prototype = prototype;
    }
}
