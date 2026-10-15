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
    public const int MaxChanges = 2048;
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
        public readonly int X = x, Y = y, Width = width, Height = height;
        public readonly Guid Token = Guid.NewGuid();
        public List<GridEditorItem> Items = new();
        public TimeSpan LastDownload;
        public int GridX, GridY, GridWidth, GridHeight;
        public readonly Dictionary<string, EntityUid> Entities = new();
        public bool Contains(float px, float py) => px >= X && py >= Y && px < X + Width && py < Y + Height;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<GridEditorApplyEvent>(Apply);
        SubscribeNetworkEvent<GridEditorCloseEvent>((_, args) => _drafts.Remove(args.SenderSession));
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
            if (session.Status != SessionStatus.InGame || !Exists(draft.Grid))
                _drafts.Remove(session);
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
        var query = AllEntityQuery<TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var transform, out var meta))
            if (transform.ParentUid == grid && !HasComp<ActorComponent>(uid) && meta.EntityPrototype != null)
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

    private List<GridEditorItem> Capture(Draft draft)
    {
        var items = new List<GridEditorItem>();
        draft.Entities.Clear();
        var grid = Comp<MapGridComponent>(draft.Grid);
        foreach (var tile in _map.GetAllTiles(draft.Grid, grid))
        {
            var p = tile.GridIndices;
            if (!draft.Contains(p.X, p.Y)) continue;
            items.Add(new GridEditorItem { Id = $"t:{p.X}:{p.Y}", Kind = "tile", Prototype = _tiles[tile.Tile.TypeId].ID,
                X = p.X, Y = p.Y, Variant = tile.Tile.Variant, Flags = tile.Tile.Flags, Mirror = tile.Tile.RotationMirroring });
        }
        var query = AllEntityQuery<TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var transform, out var meta))
        {
            if (transform.ParentUid != draft.Grid || HasComp<ActorComponent>(uid) || meta.EntityPrototype == null ||
                !draft.Contains(transform.LocalPosition.X, transform.LocalPosition.Y)) continue;
            var id = "e:" + GetNetEntity(uid);
            draft.Entities[id] = uid;
            items.Add(new GridEditorItem { Id = id, Kind = "entity", Prototype = meta.EntityPrototype.ID,
                X = transform.LocalPosition.X, Y = transform.LocalPosition.Y, Rotation = (float) transform.LocalRotation.Degrees,
                Anchored = transform.Anchored });
        }
        if (TryComp<DecalGridComponent>(draft.Grid, out var decals))
            foreach (var chunk in decals.ChunkCollection.ChunkCollection.Values)
            foreach (var (id, decal) in chunk.Decals)
                if (draft.Contains(decal.Coordinates.X, decal.Coordinates.Y))
                    items.Add(new GridEditorItem { Id = "d:" + id, Kind = "decal", Prototype = decal.Id,
                        X = decal.Coordinates.X, Y = decal.Coordinates.Y, Rotation = (float) decal.Angle.Degrees,
                        Color = (decal.Color ?? Color.White).ToHex(), Z = decal.ZIndex, Cleanable = decal.Cleanable });
        return items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    private void Apply(GridEditorApplyEvent ev, EntitySessionEventArgs args)
    {
        var player = args.SenderSession;
        void Result(string message, bool success = false) => RaiseNetworkEvent(new GridEditorResultEvent(message, success), player);
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue(player, out var draft) ||
            ev.Token != draft.Token || !Exists(draft.Grid))
        { Result("Сессия редактора недействительна или права Mapping отозваны. Черновик остаётся в окне редактора."); return; }
        if ((long) ev.Items.Count > (long) draft.Items.Count + MaxChanges) { Result("Слишком много новых элементов."); return; }
        var current = Capture(draft);
        if (current.Count != draft.Items.Count || !current.Zip(draft.Items).All(pair => pair.First.Same(pair.Second)))
        { Result("Grid изменился после открытия редактора. Закройте и откройте редактор заново."); return; }
        var original = draft.Items.ToDictionary(i => i.Id);
        var final = new Dictionary<string, GridEditorItem>();
        foreach (var item in ev.Items)
        {
            if (item.Id.Length > 80 || item.Prototype.Length > 200 || !float.IsFinite(item.X) || !float.IsFinite(item.Y) ||
                !float.IsFinite(item.Rotation) || Math.Abs(item.Rotation) > 3600 || !draft.Contains(item.X, item.Y) ||
                !final.TryAdd(item.Id, item)) { Result("Некорректные координаты или повторяющийся ID."); return; }
            var existing = original.TryGetValue(item.Id, out var old);
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
            (!original.TryGetValue(item.Id, out var old) || old.Prototype != item.Prototype || old.X != item.X || old.Y != item.Y))
            .GroupBy(item => new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y)))
            .ToDictionary(group => group.Key, group => group.Last().Id);
        foreach (var item in final.Values.ToArray())
            if (item.Kind == "entity" && GridEditorPlacement.IsExclusive(_prototypes.Index<EntityPrototype>(item.Prototype), _tags) &&
                incoming.TryGetValue(new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y)), out var winner) && winner != item.Id)
                final.Remove(item.Id);
        var walls = final.Values.Where(item => item.Kind == "entity" && GridEditorPlacement.IsWall(_prototypes.Index<EntityPrototype>(item.Prototype), _tags)).ToArray();
        var occupied = final.Values.Where(item => item.Kind == "tile").Select(item => new Vector2i((int) item.X, (int) item.Y))
            .Concat(walls.Select(item => new Vector2i((int) MathF.Floor(item.X), (int) MathF.Floor(item.Y)))).ToHashSet();
        foreach (var wall in walls)
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
            var mirror = prototype == GridEditorPlacement.DiagonalFloor ? unchecked((byte) (((int) MathF.Round(wall.Rotation / 90) % 4 + 4) % 4)) :
                existing != null && existing.Prototype == prototype ? existing.Mirror : (byte) 0;
            if (!_tiles.TryGetDefinition(prototype, out var support) || support is not ContentTileDefinition floor || floor.Abstract || floor.TileId == 0)
            { Result($"Не удалось поставить пол под стеной: отсутствует прототип {prototype}."); return; }
            if (existing != null && existing.Prototype == prototype && existing.Mirror == mirror) { final[id] = existing; continue; }
            final[id] = new GridEditorItem { Id = id, Kind = "tile", Prototype = prototype, X = x, Y = y,
                Flags = existing?.Flags ?? 0, Variant = existing != null && existing.Prototype == prototype ? existing.Variant : (byte) 0, Mirror = mirror };
        }
        // Decals and anchored entities require a supporting tile in the FINAL draft, not just in the old grid.
        bool HasFloor(GridEditorItem item) => final.ContainsKey($"t:{(int) MathF.Floor(item.X)}:{(int) MathF.Floor(item.Y)}");
        if (final.Values.FirstOrDefault(i => (i.Kind == "decal" || i.Kind == "entity" && i.Anchored) && !HasFloor(i)) is { } unsupported)
        { Result($"Под элементом {unsupported.Prototype} ({(int) MathF.Floor(unsupported.X)}, {(int) MathF.Floor(unsupported.Y)}) должен быть пол."); return; }
        var removed = original.Values.Where(i => !final.ContainsKey(i.Id)).ToList();
        var changed = final.Values.Where(i => !original.TryGetValue(i.Id, out var old) || !i.Same(old)).ToList();
        if (removed.Count + changed.Count > MaxChanges) { Result("Применяйте не более 2048 изменений за один раз."); return; }
        var gridComp = Comp<MapGridComponent>(draft.Grid);
        var remaining = _map.GetAllTiles(draft.Grid, gridComp).Count() - removed.Count(i => i.Kind == "tile") +
            changed.Count(i => i.Kind == "tile" && !original.ContainsKey(i.Id));
        if (remaining <= 0) { Result("Нельзя удалить последний тайл grid."); return; }
        var tileChanges = removed.Where(i => i.Kind == "tile").Select(i => (new Vector2i((int) i.X, (int) i.Y), Tile.Empty)).ToList();
        tileChanges.AddRange(changed.Where(i => i.Kind == "tile").Select(i =>
            (new Vector2i((int) i.X, (int) i.Y), new Tile(_tiles[i.Prototype].TileId, i.Flags, i.Variant, i.Mirror))));
        var expandedTiles = 0;
        if (ev.Expansion is { } expansion)
        {
            var bounds = gridComp.LocalAABB;
            if ((int) MathF.Floor(bounds.Left / gridComp.TileSize) != draft.GridX ||
                (int) MathF.Floor(bounds.Bottom / gridComp.TileSize) != draft.GridY ||
                (int) MathF.Ceiling(bounds.Right / gridComp.TileSize) != draft.GridX + draft.GridWidth ||
                (int) MathF.Ceiling(bounds.Top / gridComp.TileSize) != draft.GridY + draft.GridHeight)
            { Result("Границы grid изменились. Откройте редактор заново."); return; }
            if (expansion.Left is < 0 or > 32 || expansion.Right is < 0 or > 32 ||
                expansion.Bottom is < 0 or > 32 || expansion.Top is < 0 or > 32 ||
                expansion.Tile.Length > 200 || !_tiles.TryGetDefinition(expansion.Tile, out var definition) ||
                definition is not ContentTileDefinition floor || floor.Abstract || floor.TileId == 0)
            { Result("Некорректное расширение grid или прототип пола."); return; }
            var left = draft.GridX - expansion.Left;
            var bottom = draft.GridY - expansion.Bottom;
            var right = draft.GridX + draft.GridWidth + expansion.Right;
            var top = draft.GridY + draft.GridHeight + expansion.Top;
            var added = (long) (right - left) * (top - bottom) - (long) draft.GridWidth * draft.GridHeight;
            if (added > 8192 || left < -8192 || bottom < -8192 || right > 8192 || top > 8192)
            { Result("Расширение: не более 8192 новых тайлов за раз, координаты в пределах ±8192."); return; }
            var edited = tileChanges.Select(i => i.Item1).ToHashSet();
            for (var tx = left; tx < right; tx++)
            {
                // Only the new border is filled. Preserve holes and all edits in the original footprint.
                var interiorColumn = tx >= draft.GridX && tx < draft.GridX + draft.GridWidth;
                for (var ty = bottom; ty < top; ty++)
                {
                    if (interiorColumn && ty == draft.GridY) ty += draft.GridHeight;
                    if (ty >= top) break;
                    var position = new Vector2i(tx, ty);
                    if (edited.Contains(position) || !_map.GetTileRef(draft.Grid, gridComp, position).Tile.IsEmpty) continue;
                    tileChanges.Add((position, new Tile(floor.TileId)));
                    expandedTiles++;
                }
            }
        }
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
                if (item.Kind == "entity") Del(draft.Entities[item.Id]);
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
                $"{player.Name} applied grid editor draft to {ToPrettyString(draft.Grid)}: {changed.Count} changed, {removed.Count} removed, {expandedTiles} expansion tiles");
            Open(player, draft.Grid);
            Result("Изменения применены к grid. Сохраните карту штатной командой savemap / savegrid.", true);
        }
        catch (Exception e)
        {
            Log.Error($"Grid editor apply failed: {e}");
            _drafts.Remove(player);
            Result("Применение прервано; часть изменений могла сохраниться. Проверьте grid и откройте редактор заново.");
        }
    }
}
