using Content.Shared.Botany.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using JetBrains.Annotations;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Prototypes;

namespace Content.Shared.Botany.Systems;

/// <summary>
/// API for runtime plant lifecycle state.
/// </summary>
public sealed partial class PlantHolderSystem : EntitySystem
{
    [Dependency] private ISerializationManager _serialization = default!;

     private EntityQuery<PlantComponent> _plantQuery;

    public override void Initialize()
    {
        base.Initialize();
        _plantQuery = GetEntityQuery<PlantComponent>();
        SubscribeLocalEvent<PlantHolderComponent, DamageChangedEvent>(OnDamageDealt);
        InitializeRelay();
    }

    private void OnDamageDealt(EntityUid uid, PlantHolderComponent component, DamageChangedEvent args)
    {
        if (args.DamageDelta is { } delta)
            AdjustsHealth((uid, component), -delta.GetTotal().Float());
    }

    /// <summary>
    /// Adjusts the health of the plant.
    /// </summary>
    [PublicAPI]
    public void AdjustsHealth(Entity<PlantHolderComponent?> ent, float amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        if (!_plantQuery.TryComp(ent.Owner, out var plant))
            return;

        ent.Comp.Health += amount;
        ent.Comp.Health = MathHelper.Clamp(ent.Comp.Health, 0f, plant.Endurance);
        Dirty(ent);
        CheckHealth(ent);
    }

    /// <summary>
    /// Adjusts the mutation level of the plant.
    /// </summary>
    [PublicAPI]
    public void AdjustsMutationLevel(Entity<PlantHolderComponent?> ent, ProtoId<RandomPlantMutationListPrototype> mutationTableId, float amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        var mutationTable = ProtoMan.Index(mutationTableId);

        var current = ent.Comp.MutationLevels.GetValueOrDefault(mutationTableId);
        var adjustmentAmount = mutationTable.IgnoreMutationMod ? amount : amount * ent.Comp.MutationMod;

        ent.Comp.MutationLevels[mutationTableId] = MathHelper.Clamp(current + adjustmentAmount, 0f, ent.Comp.MaxMutationLevel);
        Dirty(ent);
        CheckHealth(ent);
    }

    /// <summary>
    /// Adjusts the mutation mod of the plant.
    /// </summary>
    [PublicAPI]
    public void AdjustsMutationMod(Entity<PlantHolderComponent?> ent, float amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.MutationMod += amount;
        ent.Comp.MutationMod = MathHelper.Clamp(ent.Comp.MutationMod, 0f, ent.Comp.MaxMutationMod);
        Dirty(ent);
    }

    /// <summary>
    /// Adjusts the age of the plant.
    /// </summary>
    [PublicAPI]
    public void AdjustsAge(Entity<PlantHolderComponent?> ent, int amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.Age = Math.Max(0, ent.Comp.Age + amount);
        Dirty(ent);
    }

    /// <summary>
    /// Adjusts the yield mod of the plant.
    /// </summary>
    [PublicAPI]
    public void AdjustsYieldMod(Entity<PlantHolderComponent?> ent, int amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.YieldMod += amount;
        ent.Comp.YieldMod = MathHelper.Clamp(ent.Comp.YieldMod, 1, ent.Comp.MaxYieldMod);
        Dirty(ent);
    }

    /// <summary>
    /// Adjusts the skip aging of the plant.
    /// </summary>
    [PublicAPI]
    public void AdjustsSkipAging(Entity<PlantHolderComponent?> ent, int amount)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.SkipAging = Math.Max(0, ent.Comp.SkipAging + amount);
        Dirty(ent);
    }

    /// <summary>
    /// Checks if the plant is dead.
    /// </summary>
    [PublicAPI]
    public bool IsDead(Entity<PlantHolderComponent?> ent)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return false;

        return ent.Comp.Dead;
    }

    /// <summary>
    /// Checks if the plant is dead.
    /// </summary>
    [PublicAPI]
    public void CheckHealth(Entity<PlantHolderComponent?> ent)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        if (ent.Comp.Health <= 0)
            KillPlant(ent);
    }

    /// <summary>
    /// Kills the plant.
    /// </summary>
    [PublicAPI]
    public void KillPlant(Entity<PlantHolderComponent?> ent)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.Dead = true;
        ent.Comp.Health = Math.Max(0, ent.Comp.Health);
        Dirty(ent);
    }

    /// <summary>
    /// Checks whether the plant's health is at or below half its endurance.
    /// </summary>
    [PublicAPI]
    public bool GetHealthThreshold(Entity<PlantHolderComponent?> ent)
    {
        if (!Resolve(ent.Owner, ref ent.Comp, false)
            || !_plantQuery.TryComp(ent.Owner, out var plant))
            return false;

        return ent.Comp.Health <= plant.Endurance * 0.5f;
    }
}
