using System.IO;
using System.Linq;
using Content.Server.Construction.Components;
using Content.Server.Materials;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Construction.Components;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Materials;
using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private double StoredMaterialValue(IEnumerable<EntityUid> retained)
    {
        var value = 0.0;
        foreach (var entity in retained)
        {
            if (!TryComp<MaterialStorageComponent>(entity, out var storage))
                continue;
            // Local storage only: remote ore silos are sold separately, never through each client.
            foreach (var (material, quantity) in storage.Storage)
            {
                if (quantity <= 0)
                    continue;
                if (!_prototypes.TryIndex<MaterialPrototype>(material, out var prototype))
                    throw new InvalidDataException($"Неизвестный материал в хранилище: {material}.");
                value += quantity * (double) prototype.Price;
            }
        }
        if (!double.IsFinite(value) || value < 0)
            throw new InvalidDataException("Не удалось оценить материалы шаттла.");
        return value;
    }

    /// <summary>Only mutates the disposable clone, before the durable snapshot/payout transaction.</summary>
    private void NormalizeStoredEquipment(EntityUid grid, Dictionary<EntityUid, EntityUid> origins)
    {
        var ship = Comp<ParkingShuttleComponent>(grid);
        foreach (var entity in Descendants(grid).ToArray())
            if (TryComp<TechnologyDatabaseComponent>(entity, out var database))
                _research.ResetStoredKnowledge(entity, database);
        foreach (var entity in Descendants(grid).ToArray())
        {
            if (TerminatingOrDeleted(entity))
                continue;
            if (TryComp<ShipShieldEmitterComponent>(entity, out _) && Prototype(entity) is { } current)
            {
                var target = ship.EquipmentBaselineRecorded
                    ? ship.OriginalShieldPrototype ?? "ShieldGeneratorSmall" : current.ID;
                if (TryComp<ParkingOriginalComponent>(entity, out var original))
                {
                    // An old original shield has no trustworthy purchase baseline. Preserve it rather
                    // than make a stock ship worse; newly registered ships carry the exact baseline.
                    target = original.BaselinePrototype ?? current.ID;
                    if (!_prototypes.TryIndex<EntityPrototype>(target, out var baseline) ||
                        !baseline.TryComp<ShipShieldEmitterComponent>(out _, _components))
                        target = "ShieldGeneratorSmall";
                }
                if (current.ID != target)
                    ReplaceCloneEntity(grid, entity, target, origins);
            }
        }

        foreach (var entity in Descendants(grid).ToArray())
        {
            if (TryComp<MaterialStorageComponent>(entity, out var storage))
                EntityManager.System<MaterialStorageSystem>().ClearLocalMaterials(entity, storage);
            if (TryComp<LatheComponent>(entity, out var lathe) && Prototype(entity) is { } lathePrototype &&
                lathePrototype.TryComp<LatheComponent>(out var baseLathe, _components))
                EntityManager.System<Content.Server.Lathe.LatheSystem>().SetLatheMultipliers((entity, lathe),
                    materialUse: baseLathe.MaterialUseMultiplier, time: baseLathe.TimeMultiplier);
            if (!TryComp<MachineComponent>(entity, out var machine))
                continue;
            foreach (var part in machine.PartContainer.ContainedEntities.ToArray())
            {
                if (!TryComp<MachinePartComponent>(part, out var component))
                    continue;
                var baseline = TryComp<ParkingOriginalComponent>(part, out var original)
                    ? original.BaselinePrototype : null;
                if (baseline == null || !_prototypes.TryIndex<EntityPrototype>(baseline, out var prototype) ||
                    !prototype.TryComp<MachinePartComponent>(out _, _components))
                    baseline = _prototypes.Index<MachinePartPrototype>(component.PartType).StockPartPrototype;
                if (Prototype(part)?.ID != baseline)
                    ReplaceCloneEntity(grid, part, baseline, origins);
            }
            _upgradePower.RestoreUpgradeBases(entity);
            _construction.RestoreMachineParts(entity);
        }
        // Repair blueprints must follow the same baseline policy as the saved equipment.
        // Otherwise a destroyed upgraded shield could bypass the parking downgrade by being rebuilt.
        if (TryComp<Content.Shared._Mono.ShipRepair.Components.ShipRepairDataComponent>(grid, out var repair))
            foreach (var spec in repair.Chunks.Values.SelectMany(c => c.Entities.Values))
                if (spec.ParkingBaseline is { } baseline && _prototypes.TryIndex<EntityPrototype>(baseline, out var prototype) &&
                    prototype.TryComp<ShipShieldEmitterComponent>(out _, _components))
                {
                    var index = repair.EntityPalette.IndexOf(baseline);
                    if (index < 0) { index = repair.EntityPalette.Count; repair.EntityPalette.Add(baseline); }
                    spec.ProtoIndex = index;
                }
    }

    private EntityUid ReplaceCloneEntity(EntityUid grid, EntityUid old, string prototype,
        Dictionary<EntityUid, EntityUid> origins)
    {
        var transform = Transform(old);
        var replacement = Spawn(prototype, transform.Coordinates);
        _transform.SetLocalRotation(replacement, transform.LocalRotation);
        if (_containers.TryGetContainingContainer((old, null, null), out var container))
        {
            if (!_containers.TryRemoveFromContainer(old) || !_containers.Insert(replacement, container))
                throw new InvalidDataException("Не удалось вернуть штатную деталь в её контейнер.");
        }
        else if (transform.Anchored)
            _transform.AnchorEntity(replacement);

        if (TryComp<ParkingOriginalComponent>(old, out var original))
        {
            var restored = EnsureComp<ParkingOriginalComponent>(replacement);
            restored.Shuttle = original.Shuttle;
            restored.Identity = original.Identity;
            restored.BaselinePrototype = original.BaselinePrototype;
            Comp<ParkingShuttleComponent>(grid).OriginalItems[original.Identity] = replacement;
        }
        if (origins.Remove(old, out var source))
            origins[replacement] = source;
        if (TryComp<Content.Shared._Mono.ShipRepair.Components.ShipRepairDataComponent>(grid, out var repair))
            foreach (var spec in repair.Chunks.Values.SelectMany(c => c.Entities.Values))
                if (spec.OriginalEntity == GetNetEntity(old)) spec.OriginalEntity = GetNetEntity(replacement);
        Del(old);
        return replacement;
    }
}
