using Content.Shared.Botany.Events;
using Content.Shared.Botany.Systems;
using Content.Shared.Botany.Traits.Components;

namespace Content.Shared.Botany.Traits.Systems;

/// <inheritdoc cref="PlantTraitUnviableComponent"/>
public sealed partial class PlantTraitUnviableSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlantTraitUnviableComponent, PlantGrowEvent>(OnPlantGrow);
    }

    [Dependency] private PlantHarvestSystem _plantHarvest = default!;
    [Dependency] private PlantHolderSystem _plantHolder = default!;

    private void OnPlantGrow(Entity<PlantTraitUnviableComponent> ent, ref PlantGrowEvent args)
    {
        _plantHarvest.AffectGrowth(ent.Owner, -1);
        _plantHolder.AdjustsHealth(ent.Owner, -ent.Comp.UnviableDamage);
    }
}
