using System.Threading.Tasks;
using System.Linq;
using Content.Shared.Mind.Components;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    private TimeSpan _nextWriterPreparationWarning;

    private Task<WriterPreparationResult> PrepareApartmentWritersAsync(Instance instance)
    {
        var types = new HashSet<Type> { typeof(MetaDataComponent), typeof(TransformComponent) };
        var excludedRoots = new HashSet<EntityUid>();
        foreach (var visit in _visits.Values)
        {
            excludedRoots.Add(visit.Body);
            foreach (var gear in visit.Gear) excludedRoots.Add(gear.Item);
        }
        var allowed = new Dictionary<EntityUid, bool>();
        bool Include(EntityUid entity)
        {
            if (entity == instance.Grid) return true;
            if (allowed.TryGetValue(entity, out var cached)) return cached;
            var include = Exists(entity) && !excludedRoots.Contains(entity) && !HasComp<ActorComponent>(entity) &&
                !HasComp<ApartmentChameleonGearComponent>(entity) &&
                !(TryComp<MindContainerComponent>(entity, out var mind) && mind.Mind != null) &&
                TryComp<TransformComponent>(entity, out var transform) && transform.ParentUid.IsValid() && Include(transform.ParentUid);
            allowed[entity] = include;
            return include;
        }
        var factory = _snapshotDependencies.Resolve<IComponentFactory>();
        foreach (var entity in ApartmentEntities(instance.Grid).Prepend(instance.Grid))
        {
            if (!Include(entity)) continue;
            foreach (var component in EntityManager.GetComponents(entity))
                if (component.LifeStage < ComponentLifeStage.Stopping && !factory.GetRegistration(component.GetType()).Unsaved)
                    types.Add(component.GetType());
            // Prototype defaults are written by GetProtoCache even for components removed at runtime.
            if (MetaData(entity).EntityPrototype is { } prototype)
                foreach (var entry in prototype.Components.Values) types.Add(entry.Component.GetType());
        }
        // Only materialized Type metadata crosses the worker boundary, never a component,
        // prototype instance, Instance, EntitySerializer, or deferred ECS enumeration.
        return ((SerializationManager) _snapshotDependencies.Resolve<ISerializationManager>()).PrepareWritersAsync(types);
    }
}
