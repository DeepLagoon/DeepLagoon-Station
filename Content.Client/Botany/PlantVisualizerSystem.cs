using Content.Client.Botany.Components;
using Content.Shared.Botany.Components;
using Content.Shared.Botany.Systems;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;
using Robust.Client.GameObjects;

namespace Content.Client.Botany;

public sealed partial class PlantVisualizerSystem : VisualizerSystem<PlantVisualsComponent>
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlantVisualsComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<PlantVisualsComponent, ComponentStartup>(OnComponentStartup);
        SubscribeLocalEvent<PlantComponent, AfterAutoHandleStateEvent>(OnPlantState);
        SubscribeLocalEvent<PlantHolderComponent, AfterAutoHandleStateEvent>(OnHolderState);
        SubscribeLocalEvent<PlantChemicalsComponent, AfterAutoHandleStateEvent>(OnChemicalsState);
        SubscribeLocalEvent<PlantGrowthComponent, AfterAutoHandleStateEvent>(OnGrowthState);
        SubscribeLocalEvent<PlantAtmosphericComponent, AfterAutoHandleStateEvent>(OnAtmosphericState);
        SubscribeLocalEvent<PlantConsumeExudeGasComponent, AfterAutoHandleStateEvent>(OnGasState);
    }

    [Dependency] private PlantSystem _plant = default!;
    [Dependency] private PlantHolderSystem _plantHolder = default!;
    [Dependency] private PlantTrayVisualizerSystem _plantTrayVisualizer = default!;
    [Dependency] private PlantAnalyzerSystem _plantAnalyzer = default!;

    private void OnComponentInit(EntityUid uid, PlantVisualsComponent component, ComponentInit args)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        // Ensure they always render above the tray sprite.
        SpriteSystem.SetDrawDepth((uid, sprite), (int)DrawDepth.SmallObjects);
        SpriteSystem.LayerMapReserve((uid, sprite), PlantLayers.Plant);
        SpriteSystem.LayerSetVisible((uid, sprite), PlantLayers.Plant, false);
    }

    private void OnComponentStartup(Entity<PlantVisualsComponent> ent, ref ComponentStartup args)
    {
        UpdateSprite(ent.Owner);
    }

    private void OnPlantState(Entity<PlantComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        UpdateSprite(ent.Owner);
        _plantAnalyzer.UpdatePlantUi(ent.Owner);
    }

    private void OnHolderState(Entity<PlantHolderComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        UpdateSprite(ent.Owner);
        _plantAnalyzer.UpdatePlantUi(ent.Owner);
        if (_plant.TryGetTray(ent.Owner, out var trayEnt))
            _plantTrayVisualizer.UpdateTrayWarnings(trayEnt.AsNullable());
    }

    private void OnChemicalsState(Entity<PlantChemicalsComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _plantAnalyzer.UpdatePlantUi(ent.Owner);
    }

    private void OnGrowthState(Entity<PlantGrowthComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _plantAnalyzer.UpdatePlantUi(ent.Owner);
    }

    private void OnAtmosphericState(Entity<PlantAtmosphericComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _plantAnalyzer.UpdatePlantUi(ent.Owner);
    }

    private void OnGasState(Entity<PlantConsumeExudeGasComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _plantAnalyzer.UpdatePlantUi(ent.Owner);
    }

    private void UpdateSprite(EntityUid plantUid)
    {
        if (!HasComp<PlantVisualsComponent>(plantUid)
            || !TryComp<PlantHolderComponent>(plantUid, out var holder)
            || !TryComp<SpriteComponent>(plantUid, out var sprite))
        {
            return;
        }

        string state;

        var dead = _plantHolder.IsDead(plantUid);
        var harvestReady = holder.ReadyForHarvest;
        var growthStage = _plant.GetGrowthStageValue(plantUid);

        if (dead)
            state = "dead";
        else if (harvestReady)
            state = "harvest";
        else
            state = $"stage-{growthStage}";

        var layer = SpriteSystem.LayerMapReserve((plantUid, sprite), PlantLayers.Plant);
        SpriteSystem.LayerSetVisible((plantUid, sprite), layer, true);
        SpriteSystem.LayerSetRsiState((plantUid, sprite), layer, state);
    }
}

public enum PlantLayers : byte
{
    Plant
}
