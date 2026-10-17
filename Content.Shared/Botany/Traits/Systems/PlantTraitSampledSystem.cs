using Content.Shared.Botany.Events;
using Content.Shared.Botany.Traits.Components;
using Content.Shared.Popups;

namespace Content.Shared.Botany.Traits.Systems;

/// <inheritdoc cref="PlantTraitSampledComponent"/>
public sealed partial class PlantTraitSampledSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlantTraitSampledComponent, PlantSampleAttemptEvent>(OnPlantSampleAttempt);
    }

    [Dependency] private SharedPopupSystem _popup = default!;

    private void OnPlantSampleAttempt(Entity<PlantTraitSampledComponent> ent, ref PlantSampleAttemptEvent args)
    {
        _popup.PopupCursor(Loc.GetString("plant-sample-component-already-sampled-popup"), args.User);
        args.Cancel();
    }
}
