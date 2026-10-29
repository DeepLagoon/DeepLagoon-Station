using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private void CopySelection(GridEditorCopyRequest ev, EntitySessionEventArgs args)
    {
        if (!_admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping) ||
            !_drafts.TryGetValue((args.SenderSession, ev.Token), out var draft)) return;
        var errors = new List<string>();
        var items = FreezeSelection(draft, ev.Items, errors);
        RaiseNetworkEvent(new GridEditorCopyResult(ev.Token, ev.Request, items, ev.Name, string.Join("\n", errors)), args.SenderSession);
    }

    private static GridEditorItem CopyItem(GridEditorItem item) => new()
    {
        Id = item.Id, Kind = item.Kind, Prototype = item.Prototype, X = item.X, Y = item.Y,
        Rotation = item.Rotation, Color = item.Color, Z = item.Z, Cleanable = item.Cleanable,
        Anchored = item.Anchored, Variant = item.Variant, Flags = item.Flags, Mirror = item.Mirror,
        PipeLayer = item.PipeLayer, GasPorts = item.GasPorts, EntityData = item.EntityData, EntityGroup = item.EntityGroup,
    };

    private List<GridEditorItem> FreezeSelection(Draft draft, List<GridEditorItem> items, List<string> errors)
    {
        var reserved = new HashSet<EntityUid>();
        void Include(EntityUid uid)
        {
            if (!Exists(uid) || !reserved.Add(uid)) return;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) Include(child);
        }
        foreach (var item in items.Where(item => item.Kind == "entity"))
            if (draft.KnownEntities.TryGetValue(item.Id, out var uid) && Exists(uid)) Include(uid);
        var group = Guid.NewGuid().ToString();
        var result = new List<GridEditorItem>();
        foreach (var source in items)
        {
            var item = CopyItem(source);
            try
            {
                if (item.Kind == "entity" && item.Id.StartsWith("e:", StringComparison.Ordinal))
                {
                    if (!draft.KnownEntities.TryGetValue(item.Id, out var uid) || !Exists(uid))
                        throw new InvalidDataException("Исходный объект удалён; обновите выделение.");
                    if (MetaData(uid).EntityPrototype?.ID != item.Prototype)
                        throw new InvalidDataException("Прототип исходного объекта изменился.");
                    var serializer = new EntitySerializer(_exportDependencies, new SerializationOptions
                    {
                        MissingEntityBehaviour = MissingEntityBehaviour.Ignore, ErrorOnOrphan = false, ExpectPreInit = false,
                    });
                    serializer.YamlUidMap[draft.Grid] = 0;
                    serializer.YamlIds.Add(0);
                    foreach (var entity in reserved)
                    {
                        var id = GetNetEntity(entity).Id;
                        serializer.YamlUidMap[entity] = id;
                        serializer.YamlIds.Add(id);
                    }
                    serializer.SerializeEntityRecursive(uid);
                    var data = EntityDocument(serializer.EntityData.Values.Select(entry => (MetaData(entry.Uid).EntityPrototype?.ID ?? "", entry.Node)));
                    data["root"] = ExportNumber(serializer.YamlUidMap[uid]);
                    item.EntityData = WriteExport(data);
                    if (item.EntityData.Length > 2 * 1024 * 1024) throw new InvalidDataException("Состояние объекта превышает 2 МиБ.");
                    item.EntityGroup = group;
                    item.GasPorts = CaptureGasPorts(uid);
                }
                result.Add(item);
            }
            catch (Exception e) { errors.Add($"{item.Prototype}: {e.GetBaseException().Message}"); }
        }
        return result;
    }

    private static MappingDataNode EntityDocument(IEnumerable<(string Prototype, MappingDataNode Node)> entities)
    {
        var entries = entities.ToArray();
        return new MappingDataNode
        {
            { "meta", new MappingDataNode { { "format", "7" }, { "category", "Unknown" }, { "entityCount", entries.Length.ToString(CultureInfo.InvariantCulture) } } },
            { "maps", new SequenceDataNode() }, { "grids", new SequenceDataNode() },
            { "orphans", new SequenceDataNode() }, { "nullspace", new SequenceDataNode() }, { "tilemap", new MappingDataNode() },
            { "entities", new SequenceDataNode(entries.GroupBy(entry => entry.Prototype).Select(group => (DataNode) new MappingDataNode
                { { "proto", group.Key }, { "entities", new SequenceDataNode(group.Select(entry => (DataNode) entry.Node.Copy()).ToArray()) } }).ToArray()) },
        };
    }

    private sealed record EntityFragment(GridEditorItem Item, int Root, (string Prototype, MappingDataNode Node)[] Entities);

    private EntityFragment ReadEntityData(GridEditorItem item)
    {
        if (item.EntityData.Length > 2 * 1024 * 1024) throw new InvalidDataException("Состояние объекта превышает 2 МиБ.");
        using var reader = new StringReader(item.EntityData);
        var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
        if (documents.Length != 1 || documents[0].Root is not MappingDataNode data)
            throw new InvalidDataException("Некорректное состояние компонентов.");
        var root = int.Parse(ExportValue(data, "root"), CultureInfo.InvariantCulture);
        var entries = data.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().SelectMany(group =>
            group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().Select(node => (Prototype: ExportValue(group, "proto"), Node: node.Copy()))).ToArray();
        if (entries.Length is < 1 or > 10000 || entries.Count(entry => ExportValue(entry.Node, "uid") == root.ToString(CultureInfo.InvariantCulture) && entry.Prototype == item.Prototype) != 1)
            throw new InvalidDataException("Прототип или корневой объект снимка не совпадает.");
        foreach (var entry in entries)
            if (entry.Prototype.Length > 0 && (!_prototypes.TryIndex<Robust.Shared.Prototypes.EntityPrototype>(entry.Prototype, out var proto) || proto.Abstract))
                throw new InvalidDataException($"Неизвестный прототип {entry.Prototype}.");
        return new EntityFragment(item, root, entries);
    }

    // The native loader remaps component entity references before starting the scene.
    // Retry without the failing root, so one malformed machine cannot stop the queue.
    private Dictionary<string, EntityUid> RestoreEntityData(EntityUid grid, IEnumerable<GridEditorItem> items, Action<GridEditorItem, Exception> failed)
    {
        var result = new Dictionary<string, EntityUid>();
        foreach (var group in items.Where(item => item.EntityData.Length > 0).GroupBy(item => item.EntityGroup.Length > 0 ? item.EntityGroup : item.Id))
        {
            var pending = new List<EntityFragment>();
            foreach (var item in group)
                try { pending.Add(ReadEntityData(item)); } catch (Exception e) { failed(item, e); }
            while (pending.Count > 0)
            {
                EntityDeserializer? loader = null;
                try
                {
                    var data = EntityDocument(pending.SelectMany(fragment => fragment.Entities).GroupBy(entry => ExportValue(entry.Node, "uid")).Select(entries => entries.First()));
                    foreach (var fragment in pending)
                    {
                        var root = data.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().SelectMany(entry => entry.Get<SequenceDataNode>("entities").Cast<MappingDataNode>())
                            .Single(entry => ExportValue(entry, "uid") == fragment.Root.ToString(CultureInfo.InvariantCulture));
                        var transform = ExportComponent(root, "Transform");
                        transform["parent"] = new ValueDataNode("0");
                        transform["pos"] = new ValueDataNode(FormattableString.Invariant($"{fragment.Item.X},{fragment.Item.Y}"));
                        transform["rot"] = new ValueDataNode(fragment.Item.Rotation.ToString(CultureInfo.InvariantCulture));
                        transform["anchored"] = new ValueDataNode(fragment.Item.Anchored ? "true" : "false");
                    }
                    loader = new EntityDeserializer(_exportDependencies, data, new DeserializationOptions { AssignMapIds = false, LogInvalidEntities = false });
                    if (!loader.TryProcessData()) throw new InvalidDataException("Движок отклонил состояние компонентов.");
                    var localIds = pending.SelectMany(fragment => fragment.Entities).Select(entry => int.Parse(ExportValue(entry.Node, "uid"), CultureInfo.InvariantCulture)).ToHashSet();
                    // Seed omitted references as invalid without rewriting numeric component settings.
                    void Seed(DataNode node)
                    {
                        switch (node)
                        {
                            case ValueDataNode value when int.TryParse(value.Value, out var id) && !localIds.Contains(id): loader.UidMap.TryAdd(id, EntityUid.Invalid); break;
                            case SequenceDataNode sequence: foreach (var child in sequence) Seed(child); break;
                            case MappingDataNode mapping:
                                foreach (var (key, child) in mapping)
                                {
                                    if (int.TryParse(key, out var keyId) && !localIds.Contains(keyId)) loader.UidMap.TryAdd(keyId, EntityUid.Invalid);
                                    Seed(child);
                                }
                                break;
                        }
                    }
                    Seed(data); loader.UidMap[0] = grid;
                    loader.CreateEntities(); loader.StartEntities();
                    foreach (var fragment in pending)
                    {
                        var uid = loader.UidMap[fragment.Root];
                        if (!Exists(uid)) { failed(fragment.Item, new InvalidDataException("Объект удалён во время инициализации.")); continue; }
                        result[fragment.Item.Id] = uid;
                    }
                    break;
                }
                catch (Exception e)
                {
                    var badId = loader?.CurrentReadingEntity?.YamlId;
                    if (badId == null && loader != null)
                        badId = loader.Entities.Where(pair => Exists(pair.Key) && (MetaData(pair.Key).EntityLifeStage == EntityLifeStage.Initializing ||
                            EntityManager.GetComponents(pair.Key).Any(component => component.LifeStage is ComponentLifeStage.Initializing or ComponentLifeStage.Starting)))
                            .Select(pair => (int?) pair.Value.YamlId).FirstOrDefault();
                    var bad = pending.FirstOrDefault(fragment => fragment.Entities.Any(entry => ExportValue(entry.Node, "uid") == badId?.ToString(CultureInfo.InvariantCulture))) ?? pending[0];
                    failed(bad.Item, e); pending.Remove(bad);
                    if (loader != null) foreach (var uid in loader.Entities.Keys.ToArray())
                        if (Exists(uid)) try { Del(uid); } catch (Exception cleanup) { Log.Warning($"Grid editor cleanup failed for {uid}: {cleanup.Message}"); }
                }
            }
        }
        return result;
    }
}
