using Content.Server.NodeContainer.Nodes;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.ActionBlocker;
using Content.Shared.Administration.Logs;
using Content.Shared.Atmos.Components;
using Content.Shared.Construction;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Database;
using Content.Shared.Doors.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Item;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.NodeContainer;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Mapping;

/// <summary>
/// Applies brush strokes of the mapper RCD. The server keeps no stroke state: every batch is validated
/// on its own and every cell is judged against the current state of the world, so repeating a cell never
/// creates a duplicate.
///
/// Replacement rule, per tile. Occupants are the uncontained entities touching the tile:
///  - Pipe: has a pipe node or <see cref="AtmosPipeLayersComponent"/>; its layer is the component layer (primary without one).
///  - Edge: its prototype is an edge-slot catalog entry (directional windows).
///  - Under: a grille (a window can be built on top of it) or a prototype of an under-slot entry.
///  - Structure: anchored and (is an airlock, or its prototype is a structure-slot catalog entry, or it has a hard
///    fixture whose layer contains both HighImpassable and MidImpassable: walls, windows, grilles, diagonals).
///  - Blocker: every item and mob, and anything else with a hard fixture on a layer that stops walking
///    (Impassable, High, Mid or Low: machines, tables, crates...).
///  - Everything else (cables, wall lights without hard fixtures) is ignored.
/// A structure entry replaces structures and edges and is skipped if the tile has a blocker; grilles are replaced too,
/// except by windows, which are built on top of them. An under entry (grille) replaces other grilles and is skipped if
/// the tile has a blocker or a structure that is not glass.
/// An edge entry replaces edges facing the same way and is skipped if the tile has a structure or a blocker.
/// A pipe entry replaces pipes on the same layer only (the layer comes from the stroke, not from the catalog entry);
/// other layers, blockers and structures do not matter.
/// An overlay entry (catwalks, firelocks) ignores all of the above and only skips a tile that already has the same prototype.
/// An identical occupant (same prototype, same direction if the entry rotates) makes the cell a no-op.
/// </summary>
public sealed partial class MapperRcdSystem : SharedMapperRcdSystem
{
    /// <summary>
    /// Tiles a single session may paint per second, so a modified client cannot flood the server.
    /// </summary>
    private const int MaxCellsPerSecond = 8192;

    /// <summary>
    /// Minimum time between two warning popups to the same user.
    /// </summary>
    private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(1.5);

    private static readonly ProtoId<TagPrototype> CatwalkTag = "Catwalk";
    private const string PlatingTile = "Plating";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private ITileDefinitionManager _tileDefs = default!;
    [Dependency] private IComponentFactory _compFactory = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private SharedAtmosPipeLayersSystem _pipeLayers = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private TurfSystem _turf = default!;

    private readonly Dictionary<NetUserId, (TimeSpan WindowStart, int Used)> _budgets = new();
    private readonly Dictionary<NetUserId, TimeSpan> _lastWarning = new();
    private EntityUid? _lastBlocker;
    private readonly HashSet<EntityUid> _intersecting = new();
    private readonly List<(EntityUid Uid, Kind Kind, AtmosPipeLayer Layer)> _occupants = new();

    private enum Kind : byte
    {
        Ignore,
        Pipe,
        Edge,
        Under,
        Structure,
        Blocker,
    }

    private enum CellResult : byte
    {
        Skipped,
        Placed,
        Replaced,
        Removed,
        Blocked,
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<MapperRcdStrokeEvent>(OnStroke);
        SubscribeNetworkEvent<MapperRcdConfigureEvent>(OnConfigure);
    }

    private void OnStroke(MapperRcdStrokeEvent ev, EntitySessionEventArgs args)
    {
        if (ev.Cells.Count == 0 || ev.Cells.Count > MapperRcdStrokeEvent.MaxCells)
            return;

        if (args.SenderSession.AttachedEntity is not { } user)
            return;

        var tool = GetEntity(ev.Tool);
        var gridUid = GetEntity(ev.Grid);

        if (!TryComp(tool, out MapperRcdComponent? rcd) ||
            !TryComp(user, out HandsComponent? hands) ||
            hands.ActiveHand?.HeldEntity != tool ||
            !_actionBlocker.CanInteract(user, tool))
        {
            return;
        }

        if (!TryGetEntry(ev.EntryId, out var entry) ||
            !TryComp(gridUid, out MapGridComponent? grid) ||
            !TrySpendBudget(args.SenderSession, ev.Cells.Count))
        {
            return;
        }

        var userXform = Transform(user);
        var gridXform = Transform(gridUid);

        if (userXform.MapID != gridXform.MapID)
            return;

        var userPos = _transform.GetWorldPosition(userXform);
        var rangeSquared = rcd.Range * rcd.Range;

        var direction = ev.Direction;
        if (((int) direction & 1) != 0)
            direction = Direction.South;

        var placed = 0;
        var replaced = 0;
        var removed = 0;
        var blocked = 0;
        var outOfRange = 0;
        EntityUid? blocker = null;

        foreach (var cell in ev.Cells)
        {
            var cellPos = _map.GridTileToWorldPos(gridUid, grid, cell);
            if ((cellPos - userPos).LengthSquared() > rangeSquared)
            {
                outOfRange++;
                continue;
            }

            _lastBlocker = null;

            var result = entry.Mode switch
            {
                MapperRcdMode.Tile => PaintTile(gridUid, grid, cell, entry),
                MapperRcdMode.Entity => PaintEntity(gridUid, grid, cell, entry, direction, ev.PipeLayer),
                MapperRcdMode.Deconstruct => Deconstruct(gridUid, grid, cell, entry.Filter),
                _ => CellResult.Skipped,
            };

            switch (result)
            {
                case CellResult.Placed:
                    placed++;
                    break;
                case CellResult.Replaced:
                    replaced++;
                    break;
                case CellResult.Removed:
                    removed++;
                    break;
                case CellResult.Blocked:
                    blocked++;
                    blocker ??= _lastBlocker;
                    break;
            }
        }

        if (placed + replaced + removed == 0)
        {
            Warn(args.SenderSession, user, blocked, blocker, outOfRange, rcd.Range);
            return;
        }

        _adminLogger.Add(LogType.RCD, LogImpact.Low,
            $"{ToPrettyString(user):user} used mapper RCD ({entry.EffectiveId}) on grid {ToPrettyString(gridUid)}: {ev.Cells.Count} cells, placed {placed}, replaced {replaced}, removed {removed}");
    }

    private void OnConfigure(MapperRcdConfigureEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } user)
            return;

        var tool = GetEntity(ev.Tool);
        var gridUid = GetEntity(ev.Grid);

        if (!TryComp(tool, out MapperRcdComponent? rcd) ||
            !TryComp(user, out HandsComponent? hands) ||
            hands.ActiveHand?.HeldEntity != tool ||
            !_actionBlocker.CanInteract(user, tool) ||
            !TryComp(gridUid, out MapGridComponent? grid) ||
            !TrySpendBudget(args.SenderSession, 1) ||
            Transform(user).MapID != Transform(gridUid).MapID)
        {
            return;
        }

        var cellPos = _map.GridTileToWorldPos(gridUid, grid, ev.Cell);
        if ((cellPos - _transform.GetWorldPosition(user)).LengthSquared() > rcd.Range * rcd.Range)
            return;

        _intersecting.Clear();
        _lookup.GetLocalEntitiesIntersecting(gridUid, ev.Cell, _intersecting, -0.05f, LookupFlags.Uncontained);

        var preferred = ev.Target is { } net ? GetEntity(net) : EntityUid.Invalid;
        EntityUid? chosen = null;

        foreach (var uid in _intersecting)
        {
            if (IsGone(uid) || !TryComp(uid, out AtmosPipeLayersComponent? layers) ||
                layers.PipeLayersLocked || layers.NumberOfPipeLayers <= 1)
            {
                continue;
            }

            if (uid == preferred)
            {
                chosen = uid;
                break;
            }

            if (chosen == null || uid.Id < chosen.Value.Id)
                chosen = uid;
        }

        if (chosen is not { } device)
            return;

        // No user or tool is passed on purpose: that would unanchor the device if the new layer overlaps another pipe.
        _pipeLayers.SetNextPipeLayer((device, Comp<AtmosPipeLayersComponent>(device)));

        var layer = (int) Comp<AtmosPipeLayersComponent>(device).CurrentPipeLayer + 1;
        _popup.PopupEntity(Loc.GetString("mapper-rcd-layer-set", ("device", Name(device)), ("layer", layer)), user, user);

        _adminLogger.Add(LogType.RCD, LogImpact.Low,
            $"{ToPrettyString(user):user} used mapper RCD to set the pipe layer of {ToPrettyString(device)} to {layer}");
    }

    /// <summary>
    /// Tells the user why a whole batch did nothing, so a silent skip is never a mystery.
    /// </summary>
    private void Warn(ICommonSession session, EntityUid user, int blocked, EntityUid? blocker, int outOfRange, float range)
    {
        if (blocked == 0 && outOfRange == 0)
            return;

        var now = _timing.CurTime;
        if (_lastWarning.TryGetValue(session.UserId, out var last) && now - last < WarnInterval)
            return;

        _lastWarning[session.UserId] = now;

        var message = blocked > 0 && blocker != null
            ? Loc.GetString("mapper-rcd-blocked", ("what", Name(blocker.Value)))
            : Loc.GetString("mapper-rcd-out-of-range", ("range", (int) range));

        _popup.PopupEntity(message, user, user);
    }

    private bool TrySpendBudget(ICommonSession session, int cells)
    {
        var now = _timing.CurTime;
        _budgets.TryGetValue(session.UserId, out var budget);

        if (now - budget.WindowStart >= TimeSpan.FromSeconds(1))
            budget = (now, 0);

        if (budget.Used + cells > MaxCellsPerSecond)
            return false;

        _budgets[session.UserId] = (budget.WindowStart, budget.Used + cells);
        return true;
    }

    private CellResult PaintTile(EntityUid gridUid, MapGridComponent grid, Vector2i cell, MapperRcdEntry entry)
    {
        if (entry.Prototype == null || !_tileDefs.TryGetDefinition(entry.Prototype, out var def))
            return CellResult.Skipped;

        // Setting a tile on an empty cell extends the grid; the next cell of the stroke may rely on it.
        var current = _map.GetTileRef(gridUid, grid, cell);
        if (current.Tile.TypeId == def.TileId)
            return CellResult.Skipped;

        _map.SetTile(gridUid, grid, cell, new Tile(def.TileId));
        return current.Tile.IsEmpty ? CellResult.Placed : CellResult.Replaced;
    }

    private CellResult PaintEntity(EntityUid gridUid, MapGridComponent grid, Vector2i cell, MapperRcdEntry entry, Direction direction, AtmosPipeLayer pipeLayer)
    {
        if (entry.Prototype == null || !ProtoManager.TryIndex<EntityPrototype>(entry.Prototype, out var proto))
            return CellResult.Skipped;

        var wantedDir = entry.Rotatable ? direction : Direction.South;
        var wantedLayer = AtmosPipeLayer.Primary;

        if (entry.Slot == MapperRcdSlot.Pipe && proto.TryGetComponent<AtmosPipeLayersComponent>(out var layers, _compFactory))
        {
            // The entry holds the primary layer prototype; the requested layer swaps it for its alternative.
            if (_pipeLayers.TryGetAlternativePrototype(layers, pipeLayer, out var altId) &&
                ProtoManager.TryIndex<EntityPrototype>(altId, out var altProto))
            {
                proto = altProto;
            }

            if (proto.TryGetComponent<AtmosPipeLayersComponent>(out var resolved, _compFactory))
                wantedLayer = resolved.CurrentPipeLayer;
        }

        if (entry.Slot == MapperRcdSlot.Overlay)
        {
            _intersecting.Clear();
            _lookup.GetLocalEntitiesIntersecting(gridUid, cell, _intersecting, -0.05f, LookupFlags.Uncontained);

            foreach (var uid in _intersecting)
            {
                if (!IsGone(uid) && MetaData(uid).EntityPrototype?.ID == proto.ID)
                    return CellResult.Skipped;
            }

            SpawnAnchored(proto.ID, gridUid, grid, cell, wantedDir);
            return CellResult.Placed;
        }

        GatherOccupants(gridUid, cell);

        EntityUid? blocker = null;
        EntityUid? structure = null;
        EntityUid? solid = null;

        foreach (var (uid, kind, layer) in _occupants)
        {
            switch (kind)
            {
                case Kind.Blocker:
                    blocker ??= uid;
                    break;
                case Kind.Structure:
                    structure ??= uid;

                    // A grille goes under glass but not under a wall or a door.
                    if (GetKind(uid) != MapperRcdFilter.Windows)
                        solid ??= uid;

                    break;
            }

            if (kind == WantedKind(entry.Slot) && IsIdentical(uid, proto.ID, entry.Rotatable, wantedDir) &&
                (entry.Slot != MapperRcdSlot.Pipe || layer == wantedLayer))
            {
                return CellResult.Skipped;
            }
        }

        var stopper = entry.Slot switch
        {
            MapperRcdSlot.Structure => blocker,
            MapperRcdSlot.Edge => blocker ?? structure,
            MapperRcdSlot.Under => blocker ?? solid,
            _ => null,
        };

        if (stopper != null)
        {
            _lastBlocker = stopper;
            return CellResult.Blocked;
        }

        var replaced = false;

        // Windows are built on top of grilles; every other structure replaces them.
        var keepUnder = GetProtoFilter(proto.ID) == MapperRcdFilter.Windows;

        foreach (var (uid, kind, layer) in _occupants)
        {
            var conflicts = entry.Slot switch
            {
                MapperRcdSlot.Structure => kind is Kind.Structure or Kind.Edge || kind == Kind.Under && !keepUnder,
                MapperRcdSlot.Under => kind == Kind.Under,
                MapperRcdSlot.Edge => kind == Kind.Edge && Transform(uid).LocalRotation.GetCardinalDir() == wantedDir,
                MapperRcdSlot.Pipe => kind == Kind.Pipe && layer == wantedLayer,
                _ => false,
            };

            if (!conflicts)
                continue;

            QueueDel(uid);
            replaced = true;
        }

        SpawnAnchored(proto.ID, gridUid, grid, cell, wantedDir);

        return replaced ? CellResult.Replaced : CellResult.Placed;
    }

    /// <summary>
    /// Spawns a structure on the tile and anchors it. Some prototypes are only anchored by their construction
    /// graph; left loose they would slide off the grid into space.
    /// </summary>
    private void SpawnAnchored(string protoId, EntityUid gridUid, MapGridComponent grid, Vector2i cell, Direction dir)
    {
        var spawned = Spawn(protoId, _map.GridTileToLocal(gridUid, grid, cell));
        _transform.SetLocalRotation(spawned, dir.ToAngle());

        var xform = Transform(spawned);
        if (!xform.Anchored)
            _transform.AnchorEntity(spawned, xform);
    }

    /// <summary>
    /// Removes at most one thing from the tile. Mobs, items and (except for the floor filter) tiles are never touched.
    /// Without a filter structures and edges go first, then any other anchored entity. With a filter only
    /// things of that kind qualify; the floor filter takes catwalks first and then peels the tile itself:
    /// a covering floor becomes plating, plating (or any subfloor tile) becomes empty space.
    /// Among equals the most recently created entity goes first.
    /// </summary>
    private CellResult Deconstruct(EntityUid gridUid, MapGridComponent grid, Vector2i cell, MapperRcdFilter filter)
    {
        _intersecting.Clear();
        _lookup.GetLocalEntitiesIntersecting(gridUid, cell, _intersecting, -0.05f, LookupFlags.Uncontained);

        EntityUid? primary = null;
        EntityUid? under = null;
        EntityUid? secondary = null;

        foreach (var uid in _intersecting)
        {
            // Pipes and many devices are items too, so only a loose item is left alone.
            if (IsGone(uid) || !Transform(uid).Anchored || HasComp<MobStateComponent>(uid))
                continue;

            if (filter != MapperRcdFilter.Any)
            {
                if (GetKind(uid) == filter && (primary == null || uid.Id > primary.Value.Id))
                    primary = uid;

                continue;
            }

            var kind = Classify(uid, out _);

            if (kind is Kind.Structure or Kind.Edge)
            {
                if (primary == null || uid.Id > primary.Value.Id)
                    primary = uid;
            }
            else if (kind == Kind.Under)
            {
                if (under == null || uid.Id > under.Value.Id)
                    under = uid;
            }
            else if (secondary == null || uid.Id > secondary.Value.Id)
            {
                secondary = uid;
            }
        }

        var target = primary ?? under ?? secondary;
        if (target != null)
        {
            QueueDel(target.Value);
            return CellResult.Removed;
        }

        return filter == MapperRcdFilter.Floors ? PeelTile(gridUid, grid, cell) : CellResult.Skipped;
    }

    private CellResult PeelTile(EntityUid gridUid, MapGridComponent grid, Vector2i cell)
    {
        var tileRef = _map.GetTileRef(gridUid, grid, cell);
        if (tileRef.Tile.IsEmpty)
            return CellResult.Skipped;

        var def = _turf.GetContentTileDefinition(tileRef);
        if (def.Indestructible)
            return CellResult.Skipped;

        var next = !def.IsSubFloor && _tileDefs.TryGetDefinition(PlatingTile, out var plating)
            ? new Tile(plating.TileId)
            : Tile.Empty;

        _map.SetTile(gridUid, grid, cell, next);
        return CellResult.Removed;
    }

    /// <summary>
    /// The kind of thing an entity is for filtered deletion: the catalog decides if it lists the prototype,
    /// otherwise doors, pipes and structures are told apart by their components and collision layers.
    /// </summary>
    private MapperRcdFilter GetKind(EntityUid uid)
    {
        var protoId = MetaData(uid).EntityPrototype?.ID;
        var listed = protoId == null ? MapperRcdFilter.Any : GetProtoFilter(protoId);

        if (listed != MapperRcdFilter.Any)
            return listed;

        if (HasComp<DoorComponent>(uid))
            return MapperRcdFilter.Doors;

        if (_tags.HasTag(uid, CatwalkTag))
            return MapperRcdFilter.Floors;

        switch (Classify(uid, out _))
        {
            case Kind.Pipe:
                return MapperRcdFilter.Pipes;
            case Kind.Edge:
                return MapperRcdFilter.Windows;
            case Kind.Under:
                return MapperRcdFilter.Walls;
            case Kind.Structure:
                // Walls stop light, glass does not.
                return HasOpaqueFixture(uid) ? MapperRcdFilter.Walls : MapperRcdFilter.Windows;
            default:
                return MapperRcdFilter.Any;
        }
    }

    private bool HasOpaqueFixture(EntityUid uid)
    {
        if (!TryComp(uid, out FixturesComponent? fixtures))
            return false;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (fixture.Hard && (fixture.CollisionLayer & (int) CollisionGroup.Opaque) != 0)
                return true;
        }

        return false;
    }

    private void GatherOccupants(EntityUid gridUid, Vector2i cell)
    {
        _occupants.Clear();
        _intersecting.Clear();
        _lookup.GetLocalEntitiesIntersecting(gridUid, cell, _intersecting, -0.05f, LookupFlags.Uncontained);

        foreach (var uid in _intersecting)
        {
            if (IsGone(uid))
                continue;

            var kind = Classify(uid, out var layer);
            if (kind != Kind.Ignore)
                _occupants.Add((uid, kind, layer));
        }
    }

    private bool IsGone(EntityUid uid)
    {
        return TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid);
    }

    private static Kind WantedKind(MapperRcdSlot slot)
    {
        return slot switch
        {
            MapperRcdSlot.Edge => Kind.Edge,
            MapperRcdSlot.Pipe => Kind.Pipe,
            MapperRcdSlot.Under => Kind.Under,
            _ => Kind.Structure,
        };
    }

    private bool IsIdentical(EntityUid uid, string protoId, bool rotatable, Direction dir)
    {
        if (MetaData(uid).EntityPrototype?.ID != protoId)
            return false;

        return !rotatable || Transform(uid).LocalRotation.GetCardinalDir() == dir;
    }

    private Kind Classify(EntityUid uid, out AtmosPipeLayer layer)
    {
        layer = AtmosPipeLayer.Primary;

        if (TryComp(uid, out AtmosPipeLayersComponent? layered))
        {
            layer = layered.CurrentPipeLayer;
            return Kind.Pipe;
        }

        if (TryComp(uid, out NodeContainerComponent? nodes))
        {
            foreach (var node in nodes.Nodes.Values)
            {
                if (node is PipeNode)
                    return Kind.Pipe;
            }
        }

        var protoId = MetaData(uid).EntityPrototype?.ID;

        if (protoId != null && GetEdgeProtos().Contains(protoId))
            return Kind.Edge;

        if (HasComp<SharedCanBuildWindowOnTopComponent>(uid) || protoId != null && GetUnderProtos().Contains(protoId))
            return Kind.Under;

        var stopsWalking = false;
        var structureLayer = false;

        if (TryComp(uid, out FixturesComponent? fixtures))
        {
            const int wallBits = (int) (CollisionGroup.HighImpassable | CollisionGroup.MidImpassable);
            const int walkBits = (int) (CollisionGroup.Impassable | CollisionGroup.HighImpassable |
                                        CollisionGroup.MidImpassable | CollisionGroup.LowImpassable);

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (!fixture.Hard || fixture.CollisionLayer == 0)
                    continue;

                if ((fixture.CollisionLayer & walkBits) != 0)
                    stopsWalking = true;

                if ((fixture.CollisionLayer & wallBits) == wallBits)
                    structureLayer = true;
            }
        }

        if (Transform(uid).Anchored &&
            (structureLayer || HasComp<AirlockComponent>(uid) || protoId != null && GetStructureProtos().Contains(protoId)))
        {
            return Kind.Structure;
        }

        // An anchored item (a pipe, a device) is part of the building, only a loose one is in the way.
        var looseItem = HasComp<ItemComponent>(uid) && !Transform(uid).Anchored;
        return stopsWalking || looseItem || HasComp<MobStateComponent>(uid) ? Kind.Blocker : Kind.Ignore;
    }
}
