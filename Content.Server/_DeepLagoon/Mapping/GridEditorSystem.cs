using System.Linq;
using System.IO;
using System.Text;
using System.Numerics;
using Content.Server.Administration.Managers;
using Content.Server.Decals;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Atmos.Piping.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Decals;
using Content.Shared.Maps;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Enums;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Mapping;

/// <summary>A full-grid mapper draft. Existing entities are moved in place, preserving their components.</summary>
public sealed partial class GridEditorSystem : EntitySystem
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private DecalSystem _decals = default!;
    [Dependency] private ISharedAdminLogManager _logs = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private CableSystem _cables = default!;
    private readonly Dictionary<(ICommonSession Player, Guid Token), Draft> _drafts = new();
    private sealed class Draft(EntityUid grid, int x, int y, int width, int height)
    {
        public readonly EntityUid Grid = grid;
        public int X = x, Y = y, Width = width, Height = height;
        public readonly Guid Token = Guid.NewGuid();
        public List<GridEditorItem> Items = new();
        public readonly Dictionary<string, GridEditorItem> KnownItems = new();
        public readonly Dictionary<string, GridEditorItem> Preview = new();
        public int PreviewGeneration, PreviewRevision;
        public GridEditorExpansion PreviewExpansion = new();
        public TimeSpan LastDownload;
        public Guid FileRequest;
        public StringBuilder? FilePlan;
        public int FileLength, FileIndex;
        public int GridX, GridY, GridWidth, GridHeight;
        public readonly Dictionary<string, EntityUid> Entities = new();
        public readonly Dictionary<string, EntityUid> KnownEntities = new();
        public readonly Dictionary<string, uint> KnownDecals = new();
        public bool Contains(float px, float py) => px >= X && py >= Y && px < X + Width && py < Y + Height;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<GridEditorApplyEvent>(Apply);
        SubscribeNetworkEvent<GridEditorAtmosRequest>(Atmos);
        SubscribeNetworkEvent<GridEditorEnvironmentRequest>(Environment);
        SubscribeNetworkEvent<GridEditorNameRequest>(RenameGrid);
        SubscribeNetworkEvent<GridEditorRepairConnectionsRequest>(RepairConnections);
        SubscribeNetworkEvent<GridEditorCloseEvent>((ev, args) => CloseEditor(args.SenderSession, ev.Token));
        SubscribeNetworkEvent<GridEditorDraftUpdateEvent>(Preview);
        SubscribeNetworkEvent<GridEditorRegionEvent>((ev, args) =>
        {
            if (!_drafts.TryGetValue((args.SenderSession, ev.Token), out var draft) || draft.Token != ev.Token) return;
            var message = Open(args.SenderSession, draft.Grid, ev.X, ev.Y);
            if (!message.Contains("открыт")) RaiseNetworkEvent(new GridEditorResultEvent(message, false, ev.Token), args.SenderSession);
        });
        SubscribeNetworkEvent<GridEditorDownloadEvent>(Download);
        SubscribeNetworkEvent<GridEditorDraftFileStart>(StartDraftFile);
        SubscribeNetworkEvent<GridEditorDraftFileChunk>(DraftFileChunk);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var ((session, token), draft) in _drafts.ToArray())
            if (session.Status != SessionStatus.InGame || !Exists(draft.Grid) || !_admins.HasAdminFlag(session, AdminFlags.Mapping))
                CloseEditor(session, token);
        _syncElapsed += frameTime;
        if (_syncElapsed < .5f || _drafts.Count == 0) return;
        _syncElapsed = 0;
        foreach (var grid in _drafts.Values.Select(draft => draft.Grid).Distinct().ToArray()) SynchronizeGrid(grid);
    }

    public string Open(ICommonSession player, EntityUid grid, int? regionX = null, int? regionY = null, int? regionWidth = null, int? regionHeight = null)
    {
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping)) return "Нужны права Mapping.";
        if (!TryComp<MapGridComponent>(grid, out var component)) return "Grid не найден.";
        var existing = _drafts.FirstOrDefault(pair => pair.Key.Player == player && pair.Value.Grid == grid).Value;
        if (existing != null)
        {
            PublishState(player, existing);
            PublishPreviews(grid);
            PublishEnvironment(player, existing);
            return "Редактор grid открыт.";
        }
        var bounds = component.LocalAABB;
        var x = (int) MathF.Floor(bounds.Left) - 4;
        var y = (int) MathF.Floor(bounds.Bottom) - 4;
        var right = Math.Max(x + 16, (int) MathF.Ceiling(bounds.Right) + 4);
        var top = Math.Max(y + 16, (int) MathF.Ceiling(bounds.Top) + 4);
        // Grid-owned sprites and decals may be outside its current tile footprint.
        void Include(float px, float py)
        {
            if (!float.IsFinite(px) || !float.IsFinite(py)) return;
            x = Math.Min(x, (int) MathF.Floor(px) - 4);
            y = Math.Min(y, (int) MathF.Floor(py) - 4);
            right = Math.Max(right, (int) MathF.Ceiling(px) + 5);
            top = Math.Max(top, (int) MathF.Ceiling(py) + 5);
        }
        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var uid))
            if (TryComp(uid, out TransformComponent? transform) && TryComp(uid, out MetaDataComponent? meta) &&
                !HasComp<ActorComponent>(uid) && meta.EntityPrototype != null)
                Include(transform.LocalPosition.X, transform.LocalPosition.Y);
        if (TryComp<DecalGridComponent>(grid, out var decals))
            foreach (var chunk in decals.ChunkCollection.ChunkCollection.Values)
            foreach (var (_, decal) in chunk.Decals)
                Include(decal.Coordinates.X, decal.Coordinates.Y);
        // Legacy explicit coordinates may extend the view, but never crop the real grid.
        if (regionX is { } rx && regionY is { } ry) Include(rx, ry);
        var width = right - x;
        var height = top - y;
        // Each open grid keeps its baseline and collaborative draft until its tab closes.
        var draft = new Draft(grid, x, y, width, height);
        draft.GridX = (int) MathF.Floor(bounds.Left / component.TileSize);
        draft.GridY = (int) MathF.Floor(bounds.Bottom / component.TileSize);
        draft.GridWidth = (int) MathF.Ceiling(bounds.Right / component.TileSize) - draft.GridX;
        draft.GridHeight = (int) MathF.Ceiling(bounds.Top / component.TileSize) - draft.GridY;
        draft.Items = Capture(draft);
        _drafts[(player, draft.Token)] = draft;
        PublishState(player, draft);
        PublishPreviews(grid);
        PublishEnvironment(player, draft);
        return $"Редактор grid открыт: ({x}, {y}), {width}×{height}.";
    }

    private void PublishState(ICommonSession player, Draft draft)
    {
        var initialized = Transform(draft.Grid).MapUid is { } map && TryComp<MapComponent>(map, out var mapComp) && mapComp.MapInitialized;
        RaiseNetworkEvent(new GridEditorStateEvent(GetNetEntity(draft.Grid), draft.Token, draft.X, draft.Y, draft.Width, draft.Height, draft.Items, initialized,
            draft.GridX, draft.GridY, draft.GridWidth, draft.GridHeight, SourcePath(draft.Grid)), player);
    }

    public string SourcePath(EntityUid grid)
    {
        if (!TryComp<GridMapSourceComponent>(grid, out var source) || !source.FromContent || string.IsNullOrWhiteSpace(source.Source)) return "";
        var path = new Robust.Shared.Utility.ResPath(source.Source.Replace('\\', '/')).ToRootedPath().Clean().ToString();
        return "Resources/" + path.TrimStart('/');
    }

    public string? Export(ICommonSession player, Guid token, bool wholeMap, out string error)
        => Export(player, token, wholeMap, out error, out _);

    private string? Export(ICommonSession player, Guid token, bool wholeMap, out string error, out GridEditorExportIssue? issue)
    {
        error = "";
        issue = null;
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue((player, token), out var draft) ||
            draft.Token != token || !Exists(draft.Grid))
        { error = "Нет действующей сессии Mapping."; return null; }
        var map = Transform(draft.Grid).MapUid;
        if (map == null || !HasComp<MapComponent>(map))
        { error = "Карта не найдена."; return null; }
        if (draft.LastDownload > TimeSpan.Zero && _timing.RealTime - draft.LastDownload < TimeSpan.FromSeconds(3))
        { error = "Подождите несколько секунд перед повторным скачиванием."; return null; }
        draft.LastDownload = _timing.RealTime;
        try
        {
            return SerializeNativeExport(wholeMap ? map.Value : draft.Grid, draft.Grid,
                wholeMap ? Robust.Shared.EntitySerialization.FileCategory.Map : Robust.Shared.EntitySerialization.FileCategory.Grid);
        }
        catch (ExportException e)
        {
            Log.Error($"Grid editor export failed: {e}");
            issue = e.Issue;
            error = $"Не удалось сериализовать карту. {e.Message}";
            return null;
        }
    }

    private void Download(GridEditorDownloadEvent ev, EntitySessionEventArgs args)
    {
        try
        {
            var yaml = Export(args.SenderSession, ev.Token, ev.WholeMap, out var error, out var issue);
            if (yaml == null) { RaiseNetworkEvent(new GridEditorFileEvent(ev.Request, 0, 0, [], error, issue), args.SenderSession); return; }
            var bytes = Encoding.UTF8.GetBytes(yaml);
            if (bytes.Length > 32 * 1024 * 1024)
            { RaiseNetworkEvent(new GridEditorFileEvent(ev.Request, 0, 0, [], "Карта превышает 32 МБ. Скачайте отдельный grid."), args.SenderSession); return; }
            var count = Math.Max(1, (bytes.Length + 65535) / 65536);
            for (var index = 0; index < count; index++)
                RaiseNetworkEvent(new GridEditorFileEvent(ev.Request, index, count, bytes.Skip(index * 65536).Take(65536).ToArray()), args.SenderSession);
        }
        catch (Exception e)
        {
            Log.Error($"Grid editor export failed: {e}");
            RaiseNetworkEvent(new GridEditorFileEvent(ev.Request, 0, 0, [], "Ошибка экспорта карты."), args.SenderSession);
        }
    }

    private List<GridEditorItem> Capture(Draft draft, Func<float, float, bool>? contains = null)
    {
        contains ??= draft.Contains;
        var items = new List<GridEditorItem>();
        draft.Entities.Clear();
        var grid = Comp<MapGridComponent>(draft.Grid);
        foreach (var tile in _map.GetAllTiles(draft.Grid, grid))
        {
            var p = tile.GridIndices;
            if (!contains(p.X, p.Y)) continue;
            items.Add(new GridEditorItem { Id = $"t:{p.X}:{p.Y}", Kind = "tile", Prototype = _tiles[tile.Tile.TypeId].ID,
                X = p.X, Y = p.Y, Variant = tile.Tile.Variant, Flags = tile.Tile.Flags, Mirror = tile.Tile.RotationMirroring });
        }
        var children = Transform(draft.Grid).ChildEnumerator;
        while (children.MoveNext(out var uid))
        {
            if (!TryComp(uid, out TransformComponent? transform) || !TryComp(uid, out MetaDataComponent? meta) ||
                HasComp<ActorComponent>(uid) || meta.EntityPrototype == null ||
                !contains(transform.LocalPosition.X, transform.LocalPosition.Y)) continue;
            var id = "e:" + GetNetEntity(uid);
            draft.Entities[id] = uid;
            draft.KnownEntities[id] = uid;
            items.Add(new GridEditorItem { Id = id, Kind = "entity", Prototype = meta.EntityPrototype.ID,
                X = transform.LocalPosition.X, Y = transform.LocalPosition.Y, Rotation = (float) transform.LocalRotation.Degrees,
                Anchored = transform.Anchored, Color = TryComp<AtmosPipeColorComponent>(uid, out var pipeColor) ? pipeColor.Color.ToHex() : Color.White.ToHex() });
        }
        if (TryComp<DecalGridComponent>(draft.Grid, out var decals))
            foreach (var chunk in decals.ChunkCollection.ChunkCollection.Values)
            foreach (var (id, decal) in chunk.Decals)
                if (contains(decal.Coordinates.X, decal.Coordinates.Y))
                    items.Add(new GridEditorItem { Id = "d:" + id, Kind = "decal", Prototype = decal.Id,
                        X = decal.Coordinates.X, Y = decal.Coordinates.Y, Rotation = (float) decal.Angle.Degrees,
                        Color = (decal.Color ?? Color.White).ToHex(), Z = decal.ZIndex, Cleanable = decal.Cleanable });
        foreach (var item in items) draft.KnownItems.TryAdd(item.Id, item);
        return items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    private void Apply(GridEditorApplyEvent ev, EntitySessionEventArgs args)
    {
        var player = args.SenderSession;
        void Result(string message, bool success = false) => RaiseNetworkEvent(new GridEditorResultEvent(message, success, ev.Token), player);
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue((player, ev.Token), out var draft) ||
            ev.Token != draft.Token || !Exists(draft.Grid))
        { Result("Сессия редактора недействительна или права Mapping отозваны. Черновик остаётся в окне редактора."); return; }
        SynchronizeGrid(draft.Grid);
        var left = draft.X; var bottom = draft.Y;
        var right = (long) draft.X + draft.Width; var top = (long) draft.Y + draft.Height;
        if (ev.Expansion is { } expansion)
        {
            var newLeft = (long) left - expansion.Left; var newBottom = (long) bottom - expansion.Bottom;
            right += expansion.Right; top += expansion.Top;
            if (newLeft < int.MinValue || newBottom < int.MinValue || right > int.MaxValue || top > int.MaxValue ||
                right <= newLeft || top <= newBottom ||
                right - newLeft > int.MaxValue || top - newBottom > int.MaxValue)
            { Result("Координаты области выходят за допустимый диапазон."); return; }
            left = (int) newLeft; bottom = (int) newBottom;
        }
        bool Contains(float x, float y) => x >= left && y >= bottom && x < right && y < top;
        var original = draft.Items.ToDictionary(i => i.Id);
        var final = new Dictionary<string, GridEditorItem>();
        var submitted = ev.Items;
        if (ev.Patch)
        {
            var merged = new Dictionary<string, GridEditorItem>(original);
            var seen = new HashSet<string>();
            foreach (var item in ev.Items)
            {
                if (!seen.Add(item.Id)) { Result("Повторяющийся ID элемента."); return; }
                if (item.Kind == "remove") merged.Remove(item.Id);
                else merged[item.Id] = item;
            }
            submitted = merged.Values.ToList();
        }
        foreach (var item in submitted)
        {
            if (original.TryGetValue(item.Id, out var live) && item.Same(live))
            {
                if (!final.TryAdd(item.Id, item)) { Result("Повторяющийся ID элемента."); return; }
                continue;
            }
            if (item.Id.Length > 80 || item.Prototype.Length > 200 || !float.IsFinite(item.X) || !float.IsFinite(item.Y) ||
                !float.IsFinite(item.Rotation) || Math.Abs(item.Rotation) > 3600 || !Contains(item.X, item.Y) ||
                !final.TryAdd(item.Id, item)) { Result("Некорректные координаты или повторяющийся ID."); return; }
            var existing = original.TryGetValue(item.Id, out var old) || draft.KnownItems.TryGetValue(item.Id, out old);
            if (existing && (old!.Kind != item.Kind || old.Prototype != item.Prototype && item.Kind != "tile") ||
                !existing && !item.Id.StartsWith("new_", StringComparison.Ordinal) && item.Kind != "tile")
            { Result("Некорректная ссылка на элемент."); return; }
            var valid = item.Kind switch
            {
                "tile" => item.X == MathF.Floor(item.X) && item.Y == MathF.Floor(item.Y) &&
                    item.Id == $"t:{(int) item.X}:{(int) item.Y}" && _tiles.TryGetDefinition(item.Prototype, out var tile) &&
                    tile is ContentTileDefinition definition && !definition.Abstract && tile.TileId != 0 &&
                    item.Variant < definition.Variants && item.Mirror <= 7,
                "entity" => _prototypes.TryIndex<EntityPrototype>(item.Prototype, out var entity) && !entity.Abstract &&
                    (!item.Anchored || item.X == MathF.Floor(item.X) + .5f && item.Y == MathF.Floor(item.Y) + .5f),
                "decal" => _prototypes.HasIndex<DecalPrototype>(item.Prototype) && item.Z is >= -100 and <= 100 &&
                    item.Color.Length <= 9 && Color.TryFromHex(item.Color, out _),
                _ => false
            };
            if (!valid) { Result("Неизвестный прототип или недопустимые параметры."); return; }
        }
        // Incoming constructions replace incompatible occupancy, never small carried items or decals.
        var incoming = final.Values.Where(item => item.Kind == "entity" && GridEditorPlacement.IsExclusive(_prototypes.Index<EntityPrototype>(item.Prototype), _tags) &&
            (!original.TryGetValue(item.Id, out var old) || !item.Same(old)))
            .GroupBy(item => new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y)))
            .ToDictionary(group => group.Key, group => group.Last().Id);
        foreach (var item in final.Values.ToArray())
            if (item.Kind == "entity" && GridEditorPlacement.IsExclusive(_prototypes.Index<EntityPrototype>(item.Prototype), _tags) &&
                incoming.TryGetValue(new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y)), out var winner) && winner != item.Id)
                final.Remove(item.Id);
        var walls = final.Values.Where(item => item.Kind == "entity" && GridEditorPlacement.IsWall(_prototypes.Index<EntityPrototype>(item.Prototype), _tags)).ToArray();
        var occupied = final.Values.Where(item => item.Kind == "tile").Select(item => new Vector2i((int) item.X, (int) item.Y))
            .Concat(walls.Select(item => new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y)))).ToHashSet();
        var touchedCells = final.Values.Where(item => !original.TryGetValue(item.Id, out var old) || !item.Same(old))
            .Concat(original.Values.Where(item => !final.TryGetValue(item.Id, out var next) || !item.Same(next)))
            .Where(item => item.Kind == "tile" || item.Kind == "entity" && GridEditorPlacement.IsWall(_prototypes.Index<EntityPrototype>(item.Prototype), _tags))
            .Select(item => new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y))).ToHashSet();
        foreach (var wall in walls.Where(wall => {
            var p = new Vector2i((int) MathF.Floor(wall.X), (int) MathF.Floor(wall.Y));
            return touchedCells.Contains(p) || touchedCells.Contains(p + new Vector2i(1, 0)) || touchedCells.Contains(p + new Vector2i(-1, 0)) ||
                touchedCells.Contains(p + new Vector2i(0, 1)) || touchedCells.Contains(p + new Vector2i(0, -1));
        }))
        {
            var x = (int) MathF.Floor(wall.X); var y = (int) MathF.Floor(wall.Y); var id = $"t:{x}:{y}";
            final.TryGetValue(id, out var existing);
            if (existing == null && original.TryGetValue(id, out var oldFloor)) existing = oldFloor;
            var diagonal = GridEditorPlacement.IsDiagonalWall(_prototypes.Index<EntityPrototype>(wall.Prototype), _tags);
            var authoredFull = existing != null && existing.Prototype != GridEditorPlacement.DiagonalFloor &&
                existing.Prototype != "Plating";
            var neighbors = new[] { new Vector2i(x - 1, y), new Vector2i(x + 1, y), new Vector2i(x, y - 1), new Vector2i(x, y + 1) }.Count(occupied.Contains);
            var prototype = !authoredFull && diagonal && neighbors < 3 ? GridEditorPlacement.DiagonalFloor :
                existing != null && existing.Prototype != GridEditorPlacement.DiagonalFloor ? existing.Prototype : "Plating";
            var mirror = prototype == GridEditorPlacement.DiagonalFloor ? unchecked((byte) ((((int) MathF.Round(wall.Rotation / 90) + 2) % 4 + 4) % 4)) :
                existing != null && existing.Prototype == prototype ? existing.Mirror : (byte) 0;
            if (!_tiles.TryGetDefinition(prototype, out var support) || support is not ContentTileDefinition floor || floor.Abstract || floor.TileId == 0)
            { Result($"Не удалось поставить пол под стеной: отсутствует прототип {prototype}."); return; }
            if (existing != null && existing.Prototype == prototype && existing.Mirror == mirror) { final[id] = existing; continue; }
            final[id] = new GridEditorItem { Id = id, Kind = "tile", Prototype = prototype, X = x, Y = y,
                Flags = existing?.Flags ?? 0, Variant = existing != null && existing.Prototype == prototype ? existing.Variant : (byte) 0, Mirror = mirror };
        }
        // Decals and anchored entities require a supporting tile in the FINAL draft, not just in the old grid.
        bool HasFloor(GridEditorItem item) => final.ContainsKey($"t:{(int) MathF.Floor(item.X)}:{(int) MathF.Floor(item.Y)}");
        var explicitlyRemoved = ev.Patch ? ev.Items.Where(i => i.Kind == "remove").Select(i => i.Id).ToHashSet() :
            original.Keys.Where(id => !final.ContainsKey(id)).ToHashSet();
        foreach (var item in final.Values.ToArray())
        {
            if (item.Kind != "decal" && (item.Kind != "entity" || !item.Anchored) || HasFloor(item) ||
                original.TryGetValue(item.Id, out var live) && item.Same(live)) continue;
            var floorId = $"t:{(int) MathF.Floor(item.X)}:{(int) MathF.Floor(item.Y)}";
            if (!explicitlyRemoved.Contains(floorId) && draft.KnownItems.TryGetValue(floorId, out var support) && support.Kind == "tile")
            { final[floorId] = support; continue; }
            Result($"Под элементом {item.Prototype} ({(int) MathF.Floor(item.X)}, {(int) MathF.Floor(item.Y)}) должен быть пол."); return;
        }
        var removed = original.Values.Where(i => !final.ContainsKey(i.Id)).ToList();
        var changed = final.Values.Where(i => !original.TryGetValue(i.Id, out var old) || !i.Same(old)).ToList();
        var gridComp = Comp<MapGridComponent>(draft.Grid);
        var remaining = _map.GetAllTiles(draft.Grid, gridComp).Count() - removed.Count(i => i.Kind == "tile") +
            changed.Count(i => i.Kind == "tile" && !original.ContainsKey(i.Id));
        if (remaining <= 0) { Result("Нельзя удалить последний тайл grid."); return; }
        var tileChanges = removed.Where(i => i.Kind == "tile").Select(i => (new Vector2i((int) i.X, (int) i.Y), Tile.Empty)).ToList();
        tileChanges.AddRange(changed.Where(i => i.Kind == "tile").Select(i =>
            (new Vector2i((int) i.X, (int) i.Y), new Tile(_tiles[i.Prototype].TileId, i.Flags, i.Variant, i.Mirror))));
        try
        {
            if (tileChanges.Count > 0)
            {
                // A mapper may temporarily draw disconnected islands or remove a corridor.
                // Keep this edit attached to the selected grid, just as procedural generation does.
                var canSplit = gridComp.CanSplit;
                gridComp.CanSplit = false;
                // Place destination floors first. Removing source floors can unanchor
                // entities and delete decals; do it only after their final placement.
                try { _map.SetTiles(draft.Grid, gridComp, tileChanges.Where(change => !change.Item2.IsEmpty).ToList()); }
                finally { gridComp.CanSplit = canSplit; }
            }
            foreach (var item in removed)
            {
                if (item.Kind == "entity" && draft.Entities.TryGetValue(item.Id, out var entity)) Del(entity);
                if (item.Kind == "decal") _decals.RemoveDecal(draft.Grid, draft.KnownDecals.GetValueOrDefault(item.Id, uint.Parse(item.Id[2..])));
            }
            // Release moved pipe cells as a batch so overlap checks see the final layout, not the old route.
            foreach (var item in changed.Where(item => item.Kind == "entity" && item.Anchored))
                if (draft.KnownEntities.TryGetValue(item.Id, out var pipe) && Exists(pipe) && HasComp<PipeRestrictOverlapComponent>(pipe) &&
                    TryComp<TransformComponent>(pipe, out var pipeTransform) && pipeTransform.Anchored &&
                    (pipeTransform.ParentUid != draft.Grid || pipeTransform.LocalPosition != new Vector2(item.X, item.Y) ||
                        pipeTransform.LocalRotation != Angle.FromDegrees(item.Rotation)))
                    _transform.Unanchor(pipe, pipeTransform);
            foreach (var item in changed.Where(i => i.Kind == "entity"))
            {
                var coords = new EntityCoordinates(draft.Grid, new Vector2(item.X, item.Y));
                // A live edit may have deleted or detached this entity since the
                // draft was authored. Recreate it rather than dropping mapper intent.
                var uid = draft.KnownEntities.TryGetValue(item.Id, out var existingEntity) && Exists(existingEntity)
                    ? existingEntity : Spawn(item.Prototype, coords);
                foreach (var viewer in _drafts.Values.Where(viewer => viewer.Grid == draft.Grid)) viewer.KnownEntities[item.Id] = uid;
                if (TryComp<AtmosPipeColorComponent>(uid, out var pipeColor))
                    EntityManager.System<AtmosPipeColorSystem>().SetColor(uid, pipeColor, Color.FromHex(item.Color));
                var transform = Comp<TransformComponent>(uid);
                _transform.SetLocalRotation(uid, Angle.FromDegrees(item.Rotation));
                if (item.Anchored)
                {
                    var cell = new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y));
                    var anchored = TryComp<CableComponent>(uid, out var cable)
                        ? _cables.ReanchorCable((uid, cable, transform), (draft.Grid, gridComp), cell)
                        : _transform.AnchorEntity((uid, transform, MetaData(uid)), (draft.Grid, gridComp), cell);
                    if (!anchored || !transform.Anchored)
                        throw new InvalidOperationException($"Cannot anchor {ToPrettyString(uid)} on edited grid {draft.Grid} at {cell}.");
                }
                else
                {
                    if (transform.Anchored) _transform.Unanchor(uid, transform);
                    _transform.SetCoordinates(uid, coords);
                }
                if (transform.Anchored && (transform.ParentUid != draft.Grid || transform.GridUid != draft.Grid))
                {
                    _transform.Unanchor(uid, transform);
                    throw new InvalidOperationException($"Edited entity {ToPrettyString(uid)} has an incompatible anchored parent.");
                }
                // Removing source tiles can detach the old client transform before this move arrives.
                // Replicate the parent and anchor together, including an unchanged grid parent.
                Dirty(uid, transform);
            }
            if (changed.Any(i => i.Kind == "decal")) EnsureComp<DecalGridComponent>(draft.Grid);
            foreach (var item in changed.Where(i => i.Kind == "decal"))
            {
                if (draft.KnownDecals.TryGetValue(item.Id, out var knownDecal)) _decals.RemoveDecal(draft.Grid, knownDecal);
                else if (item.Id.StartsWith("d:", StringComparison.Ordinal)) _decals.RemoveDecal(draft.Grid, uint.Parse(item.Id[2..]));
                if (!_decals.TryAddDecal(item.Prototype, new EntityCoordinates(draft.Grid, new Vector2(item.X, item.Y)), out var decalId,
                        Color.FromHex(item.Color), Angle.FromDegrees(item.Rotation), item.Z, item.Cleanable))
                    throw new InvalidOperationException("Decal placement failed");
                foreach (var viewer in _drafts.Values.Where(viewer => viewer.Grid == draft.Grid)) viewer.KnownDecals[item.Id] = decalId;
            }
            var emptyTiles = tileChanges.Where(change => change.Item2.IsEmpty).ToList();
            if (emptyTiles.Count > 0)
            {
                var canSplit = gridComp.CanSplit;
                gridComp.CanSplit = false;
                try { _map.SetTiles(draft.Grid, gridComp, emptyTiles); }
                finally { gridComp.CanSplit = canSplit; }
            }
            SupersedePreviews(draft.Grid, player, ev.Items.Select(item => item.Id).ToHashSet());
            _logs.Add(LogType.AdminMessage, LogImpact.High,
                $"{player.Name} applied grid editor draft to {ToPrettyString(draft.Grid)}: {changed.Count} changed, {removed.Count} removed");
            PublishPatch(player, draft, left, bottom, right, top);
            Result("Изменения применены к grid. Сохраните карту штатной командой savemap / savegrid.", true);
        }
        catch (Exception e)
        {
            Log.Error($"Grid editor apply failed: {e}");
            // Keep absolute draft intent on unexpected runtime failure. Remembered
            // identities above make retry update already-created items in place.
            draft.X = left; draft.Y = bottom; draft.Width = (int) (right - left); draft.Height = (int) (top - bottom);
            SynchronizeGrid(draft.Grid);
            Result("Применение прервано; часть изменений могла сохраниться. Черновик сохранён: повторите применение после устранения ошибки.");
        }
    }

    private void PublishPatch(ICommonSession player, Draft draft, int left, int bottom, long right, long top)
    {
        foreach (var viewer in _drafts.Values.Where(viewer => viewer.Grid == draft.Grid))
        {
            viewer.X = left; viewer.Y = bottom; viewer.Width = (int) (right - left); viewer.Height = (int) (top - bottom);
        }
        draft.Preview.Clear(); draft.PreviewExpansion = new(); draft.PreviewGeneration++;
        SynchronizeGrid(draft.Grid, player);
        PublishPreviews(draft.Grid);
    }
}
