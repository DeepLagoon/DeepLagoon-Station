using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.IoC;
using Robust.Shared.Map.Events;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    [Dependency] private IDependencyCollection _exportDependencies = default!;
    private Draft ExportSession(ICommonSession player, Guid token)
    {
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue((player, token), out var draft) ||
            draft.Token != token || !Exists(draft.Grid))
            throw new InvalidDataException("Нет действующей сессии Mapping.");
        return draft;
    }

    private void StartDraftFile(GridEditorDraftFileStart ev, EntitySessionEventArgs args)
    {
        try
        {
            var draft = ExportSession(args.SenderSession, ev.Token);
            if (ev.Length is < 1 or > 32 * 1024 * 1024) throw new InvalidDataException("Некорректный размер черновика.");
            if (draft.LastDownload > TimeSpan.Zero && _timing.RealTime - draft.LastDownload < TimeSpan.FromSeconds(3))
                throw new InvalidDataException("Подождите несколько секунд перед повторным скачиванием.");
            draft.LastDownload = _timing.RealTime;
            draft.FileRequest = ev.Request; draft.FileLength = ev.Length; draft.FileIndex = 0; draft.FilePlan = new StringBuilder();
            RaiseNetworkEvent(new GridEditorDraftFileChunk(ev.Token, ev.Request, 0, ""), args.SenderSession);
        }
        catch (InvalidDataException e) { DraftFileError(args.SenderSession, ev.Request, e.Message); }
    }

    private void DraftFileChunk(GridEditorDraftFileChunk ev, EntitySessionEventArgs args)
    {
        Draft? draft = null;
        try
        {
            draft = ExportSession(args.SenderSession, ev.Token);
            if (draft.FileRequest != ev.Request || draft.FilePlan == null) return;
            if (ev.Index != draft.FileIndex || ev.Text.Length != Math.Min(32768, draft.FileLength - draft.FilePlan.Length))
                throw new InvalidDataException("Нарушен порядок частей черновика.");
            draft.FilePlan.Append(ev.Text); draft.FileIndex++;
            if (draft.FilePlan.Length != draft.FileLength)
            {
                RaiseNetworkEvent(new GridEditorDraftFileChunk(ev.Token, ev.Request, draft.FileIndex, ""), args.SenderSession);
                return;
            }
            var plan = draft.FilePlan.ToString(); draft.FilePlan = null;
            var bytes = Encoding.UTF8.GetBytes(ExportDraft(args.SenderSession, ev.Token, plan));
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Карта превышает 32 МБ.");
            var count = Math.Max(1, (bytes.Length + 65535) / 65536);
            for (var index = 0; index < count; index++)
                RaiseNetworkEvent(new GridEditorFileEvent(ev.Request, index, count, bytes.Skip(index * 65536).Take(65536).ToArray()), args.SenderSession);
        }
        catch (Exception e)
        {
            if (draft?.FileRequest == ev.Request) draft.FilePlan = null;
            Log.Error($"Grid editor draft export failed: {e}");
            RaiseNetworkEvent(new GridEditorFileEvent(ev.Request, 0, 0, [],
                e is InvalidDataException or ExportException ? e.Message : $"Ошибка сериализации черновика карты: {e.GetBaseException().Message}",
                (e as ExportException)?.Issue), args.SenderSession);
        }
    }

    private void DraftFileError(ICommonSession player, Guid request, string error) =>
        RaiseNetworkEvent(new GridEditorFileEvent(request, 0, 0, [], error), player);

    private static string ExportValue(MappingDataNode node, string key) => node.Get<ValueDataNode>(key).Value;
    private static ValueDataNode ExportNumber(int number) => new(number.ToString(CultureInfo.InvariantCulture));

    private static MappingDataNode ExportComponent(MappingDataNode entity, string type)
    {
        if (!entity.TryGet<SequenceDataNode>("components", out var components)) entity["components"] = components = new();
        foreach (var child in components)
            if (child is MappingDataNode component && ExportValue(component, "type") == type) return component;
        var added = new MappingDataNode { { "type", type } }; components.Add(added); return added;
    }

    /// <summary>Serialize native state, then overlay a frozen geometry plan without changing any live entity.</summary>
    public string ExportDraft(ICommonSession player, Guid token, string yaml)
    {
        var draft = ExportSession(player, token);
        if (yaml.Length > 32 * 1024 * 1024) throw new InvalidDataException("Черновик превышает 32 МБ.");
        using var reader = new StringReader(yaml);
        var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
        if (documents.Length != 1 || documents[0].Root is not MappingDataNode plan)
            throw new InvalidDataException("Некорректный черновик.");
        var ids = plan.Get<MappingDataNode>("gridEditorIds");
        var planned = plan.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()
            .SelectMany(group => group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()
                .Select(node => (Prototype: ExportValue(group, "proto"), Node: node))).ToArray();
        var plannedGrid = planned.Single(entry => ExportValue(entry.Node, "uid") == "1").Node;
        var states = plan.TryGet<MappingDataNode>("gridEditorState", out var stateData) ? stateData : new MappingDataNode();
        var copies = new List<GridEditorItem>();
        foreach (var (prototype, node) in planned.Where(entry => ExportValue(entry.Node, "uid") != "1"))
        {
            var yamlId = ExportValue(node, "uid");
            if (ExportValue(ids, yamlId).StartsWith("e:", StringComparison.Ordinal) || !states.TryGet<MappingDataNode>(yamlId, out var state) || FieldValue(state, "entityData").Length == 0) continue;
            var transform = ExportComponent(node, "Transform");
            var position = ExportValue(transform, "pos").Split(',');
            copies.Add(new GridEditorItem { Id = ExportValue(ids, yamlId), Kind = "entity", Prototype = prototype,
                X = float.Parse(position[0], CultureInfo.InvariantCulture), Y = float.Parse(position[1], CultureInfo.InvariantCulture),
                Rotation = float.Parse(ExportValue(transform, "rot"), CultureInfo.InvariantCulture), Anchored = ExportValue(transform, "anchored") == "true",
                EntityData = FieldValue(state, "entityData"), EntityGroup = FieldValue(state, "entityGroup"),
                PipeLayer = int.Parse(FieldValue(state, "pipeLayer", "-1"), CultureInfo.InvariantCulture) });
        }
        var existing = new Dictionary<string, EntityUid>();
        foreach (var (_, idNode) in ids)
        {
            var id = ((ValueDataNode) idNode).Value;
            if (!id.StartsWith("e:")) continue;
            if (!draft.KnownEntities.TryGetValue(id, out var uid) || !Exists(uid))
                throw new InvalidDataException($"Объект {id} больше не существует. Обновите черновик перед сохранением, чтобы не потерять настройки компонентов.");
            if (!existing.TryAdd(id, uid)) throw new InvalidDataException("Повторяющийся объект черновика.");
        }
        var excluded = new HashSet<EntityUid>();
        void Exclude(EntityUid uid)
        {
            if (!Exists(uid) || !excluded.Add(uid)) return;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) Exclude(child);
        }
        foreach (var (id, uid) in draft.KnownEntities)
            if (!existing.ContainsKey(id) && Exists(uid) && Transform(uid).ParentUid == draft.Grid) Exclude(uid);

        EntityUid? temporaryMap = null;
        try
        {
            var copied = new Dictionary<string, EntityUid>();
            EntityUid? temporaryGrid = null;
            if (copies.Count > 0)
            {
                temporaryMap = _map.CreateMap(out var mapId);
                var gridEntity = _map.CreateGridEntity(mapId); temporaryGrid = gridEntity;
                var floor = new Tile(_tiles["Plating"].TileId);
                _map.SetTiles(gridEntity, gridEntity.Comp, copies.Select(item => (new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y)), floor)).Distinct().ToList());
                var errors = new List<string>();
                copied = RestoreEntityData(gridEntity, copies, (item, error) => errors.Add($"{item.Prototype}: {error.GetBaseException().Message}"));
                if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            }
            HashSet<EntityUid> roots = [draft.Grid];
            RaiseLocalEvent(new BeforeSerializationEvent(roots, [Transform(draft.Grid).MapID], FileCategory.Grid));
            var serializer = new EntitySerializer(_exportDependencies, new SerializationOptions { Category = FileCategory.Grid, ExpectPreInit = false });
            serializer.ErroringEntities.UnionWith(excluded);
            serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool serializable) => serializable &= !excluded.Contains(entity.Owner);
            SerializeForExport(serializer, roots, draft.Grid);
            var gridId = serializer.YamlUidMap[draft.Grid];
            if (temporaryGrid is { } copyGrid)
            {
                serializer.YamlUidMap[copyGrid] = gridId;
                foreach (var uid in copied.Values)
                {
                    serializer.SerializeEntityRecursive(uid);
                    serializer.Orphans.Remove(serializer.YamlUidMap[uid]);
                }
                serializer.YamlUidMap.Remove(copyGrid);
            }
            var data = serializer.Write();
            var grid = serializer.EntityData[gridId].Node;
            var groups = data.Get<SequenceDataNode>("entities");
            var nextId = serializer.YamlIds.Max() + 1;
            var count = serializer.EntityData.Count;
            foreach (var (prototype, node) in planned.Where(entry => ExportValue(entry.Node, "uid") != "1"))
            {
                var id = ExportValue(ids, ExportValue(node, "uid"));
                MappingDataNode target;
                if (id.StartsWith("e:"))
                {
                    var uid = existing[id];
                    if (!serializer.YamlUidMap.TryGetValue(uid, out var yamlId) || !serializer.EntityData.TryGetValue(yamlId, out var native))
                        throw new InvalidDataException($"Объект {id} недоступен для экспорта.");
                    if (MetaData(uid).EntityPrototype?.ID != prototype) throw new InvalidDataException("Прототип существующего объекта изменён.");
                    target = native.Node;
                }
                else if (copied.TryGetValue(id, out var copiedUid))
                    target = serializer.EntityData[serializer.YamlUidMap[copiedUid]].Node;
                else
                {
                    if (!_prototypes.HasIndex<EntityPrototype>(prototype)) throw new InvalidDataException("Неизвестный прототип объекта.");
                    target = new MappingDataNode { { "uid", ExportNumber(nextId++) } };
                    var group = groups.Cast<MappingDataNode>().FirstOrDefault(entry => ExportValue(entry, "proto") == prototype);
                    if (group == null) { group = new MappingDataNode { { "proto", prototype }, { "entities", new SequenceDataNode() } }; groups.Add(group); }
                    group.Get<SequenceDataNode>("entities").Add(target); count++;
                }
                var sourceTransform = ExportComponent(node, "Transform");
                var transform = ExportComponent(target, "Transform");
                foreach (var field in new[] { "pos", "rot", "anchored" }) transform[field] = sourceTransform[field].Copy();
                transform["parent"] = ExportNumber(gridId);
                if (states.TryGet<MappingDataNode>(ExportValue(node, "uid"), out var state) &&
                    int.TryParse(FieldValue(state, "pipeLayer", "-1"), out var layer) && layer >= 0)
                    ExportComponent(target, "AtmosPipeLayers")["pipeLayer"] = new ValueDataNode(((Content.Shared.Atmos.Components.AtmosPipeLayer) layer).ToString());
                if (_prototypes.Index<EntityPrototype>(prototype).Components.ContainsKey("AtmosPipeColor"))
                {
                    var plannedColor = ExportComponent(node, "AtmosPipeColor");
                    var color = FieldValue(plannedColor, "color", FieldValue(ExportComponent(target, "AtmosPipeColor"), "color", "#FFFFFFFF"));
                    if (!Color.TryFromHex(color, out _)) throw new InvalidDataException("Invalid pipe color.");
                    ExportComponent(target, "AtmosPipeColor")["color"] = new ValueDataNode(color);
                }
            }
            OverlayDraftTiles(data, grid, plan, plannedGrid);
            ExportComponent(grid, "DecalGrid")["chunkCollection"] = ExportComponent(plannedGrid, "DecalGrid")["chunkCollection"].Copy();
            data.Get<MappingDataNode>("meta")["entityCount"] = ExportNumber(count);
            RaiseLocalEvent(new AfterSerializationEvent(roots, data, FileCategory.Grid));
            using var writer = new StringWriter();
            new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
            return writer.ToString();
        }
        finally { if (temporaryMap is { } temporary && Exists(temporary)) Del(temporary); }
    }

    private static string FieldValue(MappingDataNode node, string key, string fallback = "") => node.TryGet<ValueDataNode>(key, out var value) ? value.Value : fallback;

    private void OverlayDraftTiles(MappingDataNode data, MappingDataNode grid, MappingDataNode plan, MappingDataNode plannedGrid)
    {
        var tilemap = data.Get<MappingDataNode>("tilemap");
        var names = tilemap.GroupBy(entry => ((ValueDataNode) entry.Value).Value)
            .ToDictionary(group => group.Key, group => int.Parse(group.First().Key, CultureInfo.InvariantCulture));
        var nextId = names.Count == 0 ? 0 : names.Values.Max() + 1;
        var remap = new Dictionary<int, int>();
        foreach (var (key, node) in plan.Get<MappingDataNode>("tilemap"))
        {
            var name = ((ValueDataNode) node).Value;
            if (!_tiles.TryGetDefinition(name, out _)) throw new InvalidDataException("Неизвестный прототип пола.");
            if (!names.TryGetValue(name, out var id))
            {
                id = nextId++; names.Add(name, id); tilemap.Add(id.ToString(CultureInfo.InvariantCulture), new ValueDataNode(name));
            }
            remap.Add(int.Parse(key, CultureInfo.InvariantCulture), id);
        }
        var chunks = ExportComponent(plannedGrid, "MapGrid").Get<MappingDataNode>("chunks").Copy();
        foreach (var (_, node) in chunks)
        {
            var chunk = (MappingDataNode) node;
            var bytes = Convert.FromBase64String(ExportValue(chunk, "tiles"));
            if (ExportValue(chunk, "version") != "7" || bytes.Length != 16 * 16 * 7) throw new InvalidDataException("Некорректный чанк пола.");
            for (var offset = 0; offset < bytes.Length; offset += 7)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), remap[BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4))]);
            chunk["tiles"] = new ValueDataNode(Convert.ToBase64String(bytes));
        }
        ExportComponent(grid, "MapGrid")["chunks"] = chunks;
    }
}
