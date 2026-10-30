using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    [Dependency] private IDependencyCollection _snapshotDependencies = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    private TimeSpan _nextCheckpoint;
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(60);

    private IApartmentLayoutStore OpenStore()
    {
        var store = new ApartmentLayoutStore(_resources.UserData.RootDir is { } root
            ? Path.Combine(root, "apartments-prototype.db") : ":memory:");
        // An interrupted server also ends its round. Finalize trash in unloaded apartments.
        store.FinishRound();
        return store;
    }

    private IEnumerable<EntityUid> ApartmentEntities(EntityUid parent)
    {
        var children = Transform(parent).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            yield return child;
            foreach (var descendant in ApartmentEntities(child)) yield return descendant;
        }
    }

    private IEnumerable<EntityUid> TrashContents(Instance instance)
    {
        // Empty apartments are paused; normal entity queries intentionally skip them.
        foreach (var entity in ApartmentEntities(instance.Grid))
        {
            if (!HasComp<ApartmentTrashComponent>(entity) || !TryComp<ContainerManagerComponent>(entity, out var manager)) continue;
            foreach (var container in manager.Containers.Values)
            foreach (var item in container.ContainedEntities)
                yield return item;
        }
    }

    private (string Current, string RoundEnd) SerializeApartment(Instance instance)
    {
        var excluded = new HashSet<EntityUid>();
        void Exclude(EntityUid uid)
        {
            if (!Exists(uid) || !excluded.Add(uid)) return;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) Exclude(child);
        }
        foreach (var visit in _visits.Values) Exclude(visit.Body);
        foreach (var visit in _visits.Values)
            foreach (var gear in visit.Gear) Exclude(gear.Item);
        var minds = EntityQueryEnumerator<MindComponent>();
        while (minds.MoveNext(out var uid, out _)) Exclude(uid);
        var actors = EntityQueryEnumerator<ActorComponent>();
        while (actors.MoveNext(out var uid, out _)) Exclude(uid);
        bool Excluded(EntityUid uid)
        {
            while (uid.IsValid() && uid != instance.Grid)
            {
                if (excluded.Contains(uid) || HasComp<ActorComponent>(uid) ||
                    TryComp<MindContainerComponent>(uid, out var mind) && mind.Mind != null) return true;
                if (!TryComp<TransformComponent>(uid, out var transform)) return true;
                if (!transform.ParentUid.IsValid())
                    return HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid);
                uid = transform.ParentUid;
            }
            return uid != instance.Grid;
        }
        HashSet<EntityUid> roots = [instance.Grid];
        RaiseLocalEvent(new BeforeSerializationEvent(roots, [Transform(instance.Grid).MapID], FileCategory.Grid));
        var serializer = new EntitySerializer(_snapshotDependencies, new SerializationOptions
        {
            Category = FileCategory.Grid, ExpectPreInit = false, MissingEntityBehaviour = MissingEntityBehaviour.IncludeNullspace,
        });
        serializer.ErroringEntities.UnionWith(excluded);
        serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool serializable) => serializable &= !Excluded(entity.Owner);
        foreach (var (id, entity) in instance.Furniture)
            if (Exists(entity)) EnsureComp<ApartmentFurnitureIdentityComponent>(entity).Id = id;
        serializer.SerializeEntityRecursive(roots);
        var data = serializer.Write();
        foreach (var entity in serializer.EntityData.Values)
        {
            if (!entity.Node.TryGet<SequenceDataNode>("components", out var components)) continue;
            foreach (var component in components.Cast<MappingDataNode>())
            {
                var type = component.Get<ValueDataNode>("type").Value;
                if (type == "Strap" && component.TryGet<SequenceDataNode>("buckledEntities", out var buckled))
                    foreach (var entry in buckled.Cast<ValueDataNode>().Where(entry => entry.Value == "invalid").ToArray()) buckled.Remove(entry);
                if (type != "ContainerContainer" || !component.TryGet<MappingDataNode>("containers", out var containers)) continue;
                foreach (var container in containers.Values.Cast<MappingDataNode>())
                    if (container.TryGet<SequenceDataNode>("ents", out var contents))
                        foreach (var entry in contents.Cast<ValueDataNode>().Where(entry => entry.Value == "invalid").ToArray()) contents.Remove(entry);
            }
        }
        RaiseLocalEvent(new AfterSerializationEvent(roots, data, FileCategory.Grid));
        var current = WriteApartment(data);
        var trash = new HashSet<EntityUid>();
        void CollectTrash(EntityUid uid)
        {
            if (!Exists(uid) || !trash.Add(uid)) return;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) CollectTrash(child);
        }
        foreach (var item in TrashContents(instance)) CollectTrash(item);
        if (trash.Count == 0) return (current, current);
        var removedIds = trash.Where(serializer.YamlUidMap.ContainsKey).Select(uid => serializer.YamlUidMap[uid].ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        // Derive the clean save from the complete one. Serializing excluded dictionary keys
        // directly would collapse several items into the same "invalid" EntityUid key.
        var count = 0;
        foreach (var group in data.Get<SequenceDataNode>("entities").Cast<MappingDataNode>())
        {
            var entries = group.Get<SequenceDataNode>("entities");
            foreach (var entry in entries.Cast<MappingDataNode>().ToArray())
                if (removedIds.Contains(entry.Get<ValueDataNode>("uid").Value)) entries.Remove(entry);
            count += entries.Count;
        }
        data.Get<MappingDataNode>("meta")["entityCount"] = new ValueDataNode(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var field in new[] { "orphans", "nullspace" })
        foreach (var entry in data.Get<SequenceDataNode>(field).Cast<ValueDataNode>().ToArray())
            if (removedIds.Contains(entry.Value)) data.Get<SequenceDataNode>(field).Remove(entry);
        {
            foreach (var bin in ApartmentEntities(instance.Grid).Where(entity => HasComp<ApartmentTrashComponent>(entity)))
            {
                if (!serializer.YamlUidMap.TryGetValue(bin, out var id) ||
                    !serializer.EntityData.TryGetValue(id, out var entity) ||
                    !entity.Node.TryGet<SequenceDataNode>("components", out var components)) continue;
                foreach (var component in components.Cast<MappingDataNode>())
                {
                    if (component.Get<ValueDataNode>("type").Value == "Storage") component["storedItems"] = new MappingDataNode();
                    if (component.Get<ValueDataNode>("type").Value != "ContainerContainer" ||
                        !component.TryGet<MappingDataNode>("containers", out var containers)) continue;
                    foreach (var container in containers.Values.Cast<MappingDataNode>()) container["ents"] = new SequenceDataNode();
                }
            }
        }
        return (current, WriteApartment(data));
    }

    private static string WriteApartment(MappingDataNode data)
    {
        using var writer = new StringWriter();
        new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();
    }

    public bool SaveApartment(Instance instance, bool discardTrash = false, Guid delivery = default)
    {
        if (!Exists(instance.Grid)) return false;
        try
        {
            if (discardTrash)
                foreach (var item in TrashContents(instance).ToArray()) if (Exists(item)) Del(item);
            var layout = ApartmentLayout.Diff(instance.Template, CurrentLayout(instance), instance.Delta.Revision, MapBaseline(instance));
            layout.OxygenPercent = instance.Delta.OxygenPercent;
            var (current, clean) = SerializeApartment(instance);
            var snapshot = new ApartmentSnapshot { Grid = current, Layout = layout, MapFurniture = instance.MapFurniture.Values.ToList() };
            var roundEnd = new ApartmentSnapshot { Grid = clean, Layout = layout, MapFurniture = snapshot.MapFurniture };
            Store.SaveSnapshot(instance.Owner.UserId, instance.Template.ID, snapshot, roundEnd, delivery);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Apartment snapshot failed for {instance.Owner}/{instance.Template.ID}; previous save retained: {exception}");
            return false;
        }
    }

    private Instance RestoreApartment(Robust.Shared.Network.NetUserId owner, ApartmentTemplatePrototype template, ApartmentDelta selected, ApartmentSnapshot snapshot)
    {
        using var reader = new StringReader(snapshot.Grid);
        if (!_mapLoader.TryLoadGrid(Comp<MapComponent>(_activeMap).MapId, reader, $"apartment {owner}/{template.ID}", out var grid,
                new DeserializationOptions { StoreYamlUids = true }))
            throw new InvalidDataException("Cannot restore apartment snapshot.");
        try
        {
            var layout = snapshot.Layout;
            layout.Revision = selected.Revision;
            EnsureComp<ApartmentAtmosphereSettingsComponent>(grid.Value.Owner).OxygenPercent = Math.Clamp(layout.OxygenPercent, 0, 100);
            var position = FindFreePosition(template);
            _transform.SetCoordinates(grid.Value.Owner, new EntityCoordinates(_activeMap, position));
            var instance = new Instance { Owner = owner, Template = template, Grid = grid.Value.Owner, Position = position, Delta = layout,
                Guests = _guestAccess.GetValueOrDefault(owner) ?? new ApartmentGuestAccess() };
            foreach (var mapped in snapshot.MapFurniture) instance.MapFurniture.Add(mapped.Baseline.Id, mapped);
            foreach (var entity in ApartmentEntities(instance.Grid))
                if (TryComp<ApartmentFurnitureIdentityComponent>(entity, out var identity)) instance.Furniture.Add(identity.Id, entity);
            return instance;
        }
        catch { Del(grid.Value.Owner); throw; }
    }

    private void SaveApartments(bool discardTrash)
    {
        foreach (var instance in _instances.Values.Concat(_retiring).Distinct().ToArray()) SaveApartment(instance, discardTrash);
        if (discardTrash) Store.FinishRound();
    }

    private void SpillFurnitureContents(EntityUid entity, EntityUid grid)
    {
        if (!TryComp<ContainerManagerComponent>(entity, out var manager)) return;
        var position = Transform(entity).LocalPosition;
        foreach (var container in manager.Containers.Values)
        foreach (var item in container.ContainedEntities.ToArray())
        {
            _containers.Remove(item, container, force: true);
            _transform.SetCoordinates(item, new EntityCoordinates(grid, position));
        }
    }
}
