using Content.Shared.Botany.Components;
using Content.Shared.Botany.Events;
using Content.Shared.Random.Helpers;
using JetBrains.Annotations;
using Robust.Shared.Timing;

namespace Content.Shared.Botany.Systems;

/// <summary>
/// Manages weed growth and pest damage per growth tick, and handles tray-level
/// weed spawning.
/// </summary>
public sealed partial class PlantWeedPestSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        _trayQuery = GetEntityQuery<PlantTrayComponent>();
        SubscribeLocalEvent<PlantWeedPestComponent, PlantCrossPollinateEvent>(OnCrossPollinate);
        SubscribeLocalEvent<PlantWeedPestComponent, PlantGrowEvent>(OnPlantGrow);
    }

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private BotanySystem _botany = default!;
    [Dependency] private PlantMutationSystem _mutation = default!;
    [Dependency] private PlantHolderSystem _plantHolder = default!;
    [Dependency] private PlantTraySystem _plantTray = default!;

     private EntityQuery<PlantTrayComponent> _trayQuery;

    private void OnCrossPollinate(Entity<PlantWeedPestComponent> ent, ref PlantCrossPollinateEvent args)
    {
        if (!_botany.TryGetPlantComponent<PlantWeedPestComponent>(args.PollenData, args.PollenProtoId, out var pollenData))
            return;

        _mutation.CrossFloat(ref ent.Comp.WeedTolerance, pollenData.WeedTolerance);
        _mutation.CrossFloat(ref ent.Comp.PestTolerance, pollenData.PestTolerance);
        Dirty(ent);
    }

    private void OnPlantGrow(Entity<PlantWeedPestComponent> ent, ref PlantGrowEvent args)
    {
        var trayUid = args.Tray;
        if (!_trayQuery.TryComp(trayUid, out var tray))
            return;

        if (SharedRandomExtensions.PredictedProb(_timing, ent.Comp.PestGrowthChance, GetNetEntity(ent)))
            _plantTray.AdjustPest((trayUid, tray), ent.Comp.PestGrowthAmount);

        if (tray.WeedLevel > ent.Comp.WeedTolerance)
            _plantHolder.AdjustsHealth(ent.Owner, -ent.Comp.WeedDamageAmount);

        if (tray.PestLevel > ent.Comp.PestTolerance)
            _plantHolder.AdjustsHealth(ent.Owner, -ent.Comp.PestDamageAmount);
    }

    /// <summary>
    /// Adjusts maximum weed level the plant can tolerate before taking damage.
    /// </summary>
    [PublicAPI]
    public void AdjustWeedTolerance(Entity<PlantWeedPestComponent?> ent, float amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp, false))
            return;

        ent.Comp.WeedTolerance = MathF.Max(0f, ent.Comp.WeedTolerance + amount);
        Dirty(ent);
    }

    /// <summary>
    /// Adjusts maximum pest level the plant can tolerate before taking damage.
    /// </summary>
    [PublicAPI]
    public void AdjustPestTolerance(Entity<PlantWeedPestComponent?> ent, float amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp, false))
            return;

        ent.Comp.PestTolerance = MathF.Max(0f, ent.Comp.PestTolerance + amount);
        Dirty(ent);
    }
}
