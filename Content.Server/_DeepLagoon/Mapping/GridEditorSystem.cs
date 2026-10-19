using System.Linq;
using System.IO;
using System.Text;
using System.Numerics;
using Content.Server.Administration.Managers;
using Content.Server.Decals;
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
    private readonly Dictionary<ICommonSession, Draft> _drafts = new();
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
        public int GridX, GridY, GridWidth, GridHeight;
        public readonly Dictionary<string, EntityUid> Entities = new();
        public bool Contains(float px, float py) => px >= X && py >= Y && px < X + Width && py < Y + Height;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<GridEditorApplyEvent>(Apply);
        SubscribeNetworkEvent<GridEditorAtmosRequest>(Atmos);
        SubscribeNetworkEvent<GridEditorCloseEvent>((_, args) => CloseEditor(args.SenderSession));
        SubscribeNetworkEvent<GridEditorDraftUpdateEvent>(Preview);
        SubscribeNetworkEvent<GridEditorRegionEvent>((ev, args) =>
        {
            if (!_drafts.TryGetValue(args.SenderSession, out var draft) || draft.Token != ev.Token) return;
            var message = Open(args.SenderSession, draft.Grid, ev.X, ev.Y);
            if (!message.Contains("открыт")) RaiseNetworkEvent(new GridEditorResultEvent(message, false), args.SenderSession);
        });
        SubscribeNetworkEvent<GridEditorDownloadEvent>(Download);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var (session, draft) in _drafts.ToArray())
            if (session.Status != SessionStatus.InGame || !Exists(draft.Grid) || !_admins.HasAdminFlag(session, AdminFlags.Mapping))
                CloseEditor(session);
        _syncElapsed += frameTime;
        if (_syncElapsed < .5f || _drafts.Count == 0) return;
        _syncElapsed = 0;
        foreach (var grid in _drafts.Values.Select(draft => draft.Grid).Distinct().ToArray()) SynchronizeGrid(grid);
    }

    public string Open(ICommonSession player, EntityUid grid, int? regionX = null, int? regionY = null, int? regionWidth = null, int? regionHeight = null)
    {
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping)) return "Нужны права Mapping.";
        if (!TryComp<MapGridComponent>(grid, out var component)) return "Grid не найден.";
        if (HasComp<ApartmentGridComponent>(grid)) return "Квартиры редактируются через apartment edit.";
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
            if (TryComp<TransformComponent>(uid, out var transform) && TryComp<MetaDataComponent>(uid, out var meta) &&
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
        // The browser owns the unsaved edits and may stay open for hours. Keep
        // its bounded, one-per-player baseline until close, disconnect or grid deletion.
        CloseEditor(player);
        var draft = new Draft(grid, x, y, width, height);
        draft.GridX = (int) MathF.Floor(bounds.Left / component.TileSize);
        draft.GridY = (int) MathF.Floor(bounds.Bottom / component.TileSize);
        draft.GridWidth = (int) MathF.Ceiling(bounds.Right / component.TileSize) - draft.GridX;
        draft.GridHeight = (int) MathF.Ceiling(bounds.Top / component.TileSize) - draft.GridY;
        draft.Items = Capture(draft);
        _drafts[player] = draft;
        var initialized = Transform(grid).MapUid is { } map && TryComp<MapComponent>(map, out var mapComp) && mapComp.MapInitialized;
        RaiseNetworkEvent(new GridEditorStateEvent(GetNetEntity(grid), draft.Token, x, y, width, height, draft.Items, initialized,
            draft.GridX, draft.GridY, draft.GridWidth, draft.GridHeight), player);
        PublishPreviews(grid);
        return $"Редактор grid открыт: ({x}, {y}), {width}×{height}.";
    }

    public string? Export(ICommonSession player, Guid token, bool wholeMap, out string error)
    {
        error = "";
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue(player, out var draft) ||
            draft.Token != token || !Exists(draft.Grid))
        { error = "Нет действующей сессии Mapping."; return null; }
        var map = Transform(draft.Grid).MapUid;
        if (map == null || !TryComp<MapComponent>(map, out var component) || component.MapInitialized)
        { error = "Карта уже инициализирована. Откройте исходную карту в режиме mapping перед сохранением."; return null; }
        if (draft.LastDownload > TimeSpan.Zero && _timing.RealTime - draft.LastDownload < TimeSpan.FromSeconds(3))
        { error = "Подождите несколько секунд перед повторным скачиванием."; return null; }
        draft.LastDownload = _timing.RealTime;
        using var writer = new StringWriter();
        if (!(wholeMap ? _loader.TrySaveMap(map.Value, writer) : _loader.TrySaveGrid(draft.Grid, writer)))
        { error = "Не удалось сериализовать карту."; return null; }
        return writer.ToString();
    }

    private void Download(GridEditorDownloadEvent ev, EntitySessionEventArgs args)
    {
        try
        {
            var yaml = Export(args.SenderSession, ev.Token, ev.WholeMap, out var error);
            if (yaml == null) { RaiseNetworkEvent(new GridEditorFileEvent(ev.Request, 0, 0, [], error), args.SenderSession); return; }
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
            if (!TryComp<TransformComponent>(uid, out var transform) || !TryComp<MetaDataComponent>(uid, out var meta) ||
                HasComp<ActorComponent>(uid) || meta.EntityPrototype == null ||
                !contains(transform.LocalPosition.X, transform.LocalPosition.Y)) continue;
            var id = "e:" + GetNetEntity(uid);
            draft.Entities[id] = uid;
            items.Add(new GridEditorItem { Id = id, Kind = "entity", Prototype = meta.EntityPrototype.ID,
                X = transform.LocalPosition.X, Y = transform.LocalPosition.Y, Rotation = (float) transform.LocalRotation.Degrees,
                Anchored = transform.Anchored });
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
        void Result(string message, bool success = false) => RaiseNetworkEvent(new GridEditorResultEvent(message, success), player);
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue(player, out var draft) ||
            ev.Token != draft.Token || !Exists(draft.Grid))
        { Result("Сессия редактора недействительна или права Mapping отозваны. Черновик остаётся в окне редактора."); return; }
        SynchronizeGrid(draft.Grid);
        var left = draft.X; var bottom = draft.Y;
        var right = (long) draft.X + draft.Width; var top = (long) draft.Y + draft.Height;
        if (ev.Expansion is { } expansion)
        {
            if (expansion.Left < 0 || expansion.Right < 0 || expansion.Bottom < 0 || expansion.Top < 0)
            { Result("Некорректные границы области редактирования."); return; }
            var newLeft = (long) left - expansion.Left; var newBottom = (long) bottom - expansion.Bottom;
            right += expansion.Right; top += expansion.Top;
            if (newLeft < int.MinValue || newBottom < int.MinValue || right > int.MaxValue || top > int.MaxValue ||
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
                try { _map.SetTiles(draft.Grid, gridComp, tileChanges); }
                finally { gridComp.CanSplit = canSplit; }
            }
            foreach (var item in removed)
            {
                if (item.Kind == "entity" && draft.Entities.TryGetValue(item.Id, out var entity)) Del(entity);
                if (item.Kind == "decal") _decals.RemoveDecal(draft.Grid, uint.Parse(item.Id[2..]));
            }
            foreach (var item in changed.Where(i => i.Kind == "entity"))
            {
                var coords = new EntityCoordinates(draft.Grid, new Vector2(item.X, item.Y));
                var uid = original.ContainsKey(item.Id) ? draft.Entities[item.Id] : Spawn(item.Prototype, coords);
                var transform = Comp<TransformComponent>(uid);
                if (transform.Anchored) _transform.Unanchor(uid, transform);
                _transform.SetCoordinates(uid, coords);
                _transform.SetLocalRotation(uid, Angle.FromDegrees(item.Rotation));
                if (item.Anchored) _transform.AnchorEntity(uid);
            }
            foreach (var item in changed.Where(i => i.Kind == "decal"))
            {
                if (original.ContainsKey(item.Id)) _decals.RemoveDecal(draft.Grid, uint.Parse(item.Id[2..]));
                if (!_decals.TryAddDecal(item.Prototype, new EntityCoordinates(draft.Grid, new Vector2(item.X, item.Y)), out _,
                        Color.FromHex(item.Color), Angle.FromDegrees(item.Rotation), item.Z, item.Cleanable))
                    throw new InvalidOperationException("Decal placement failed");
            }
            _logs.Add(LogType.AdminMessage, LogImpact.High,
                $"{player.Name} applied grid editor draft to {ToPrettyString(draft.Grid)}: {changed.Count} changed, {removed.Count} removed");
            PublishPatch(player, draft, left, bottom, right, top);
            Result("Изменения применены к grid. Сохраните карту штатной командой savemap / savegrid.", true);
        }
        catch (Exception e)
        {
            Log.Error($"Grid editor apply failed: {e}");
            PublishPatch(player, draft, left, bottom, right, top);
            Result("Применение прервано; часть изменений могла сохраниться. Состояние редактора обновлено, проверьте grid.");
        }
    }

    private void PublishPatch(ICommonSession player, Draft draft, int left, int bottom, long right, long top)
    {
        draft.X = left; draft.Y = bottom; draft.Width = (int) (right - left); draft.Height = (int) (top - bottom);
        draft.Preview.Clear(); draft.PreviewExpansion = new(); draft.PreviewGeneration++;
        SynchronizeGrid(draft.Grid, player);
        PublishPreviews(draft.Grid);
    }
}
