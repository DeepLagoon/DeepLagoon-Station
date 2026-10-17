using Content.Shared.Botany.Events;
using Content.Shared.Botany.Traits.Components;
using Robust.Shared.Audio.Systems;

namespace Content.Shared.Botany.Traits.Systems;

/// <inheritdoc cref="PlantTraitScreamComponent"/>
public sealed partial class PlantTraitScreamSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlantTraitScreamComponent, PlantHarvestedEvent>(OnPlantHarvested);
    }

    [Dependency] private SharedAudioSystem _audio = default!;

    private void OnPlantHarvested(Entity<PlantTraitScreamComponent> ent, ref PlantHarvestedEvent args)
    {
        _audio.PlayPredicted(ent.Comp.ScreamSound, ent, args.User);
    }
}
