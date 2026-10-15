using Content.Client.Construction;
using Content.Client.Examine;
using Content.Client.Interaction;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Input;
using Content.Shared.Popups;
using Content.Shared.RCD.Systems;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Mapping;

/// <summary>
/// Turns holding the use button with a mapper RCD into a stroke. Three kinds, picked by the modifier held
/// when the button goes down:
///  - brush (none): tiles the cursor passes over are collected, gaps between frames are filled with a Bresenham
///    line, each tile once per stroke, and sent in batches at most once per tick;
///  - line (shift): a straight line from the pressed tile to the tile under the cursor, sent on release;
///  - area (control): the rectangle between the pressed tile and the cursor tile, sent on release.
/// With a modifier held the engine does not raise the use function at all but the one bound to that combination
/// (examine for shift, pull for control), so each kind listens to its own function.
/// Control with the middle button cycles the pipe layer of the device under the cursor.
/// A click is swallowed only while the tool is in the active hand with an entry selected, so the item never
/// gets in the way of normal interaction otherwise. Losing window focus, switching the item or the entry,
/// or opening the menu drops the stroke without sending anything more.
/// </summary>
public sealed partial class MapperRcdBrushSystem : EntitySystem
{
    /// <summary>
    /// Upper bound of distinct tiles in one brush stroke.
    /// </summary>
    private const int MaxStrokeCells = 16384;

    /// <summary>
    /// Upper bound of tiles in a line or an area.
    /// </summary>
    private const int MaxShapeCells = 4096;

    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlacementManager _placement = default!;
    [Dependency] private IPrototypeManager _protos = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private InputSystem _inputSystem = default!;
    [Dependency] private MapperRcdSystem _catalog = default!;
    [Dependency] private RCDSystem _rcd = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private enum StrokeKind : byte
    {
        Brush,
        Line,
        Area,
    }

    private readonly HashSet<Vector2i> _visited = new();
    private readonly List<Vector2i> _pending = new();
    private readonly List<Vector2i> _shape = new();

    private bool _active;
    private BoundKeyFunction _key = EngineKeyFunctions.Use;
    private StrokeKind _kind;
    private EntityUid _tool;
    private EntityUid _grid;
    private string _entryId = string.Empty;
    private AtmosPipeLayer _pipeLayer;
    private Vector2i _start;
    private Vector2i _last;
    private GameTick _lastFlushTick;

    public override void Initialize()
    {
        base.Initialize();

        UpdatesOutsidePrediction = true;

        CommandBinds.Builder
            .BindBefore(EngineKeyFunctions.Use,
                new PointerInputCmdHandler((in PointerInputCmdHandler.PointerInputCmdArgs args) => OnKey(args, EngineKeyFunctions.Use),
                    ignoreUp: false, outsidePrediction: true),
                typeof(ConstructionSystem), typeof(DragDropSystem))
            .BindBefore(ContentKeyFunctions.ExamineEntity,
                new PointerInputCmdHandler((in PointerInputCmdHandler.PointerInputCmdArgs args) => OnKey(args, ContentKeyFunctions.ExamineEntity),
                    ignoreUp: false, outsidePrediction: true),
                typeof(ExamineSystem))
            .Bind(ContentKeyFunctions.TryPullObject,
                new PointerInputCmdHandler((in PointerInputCmdHandler.PointerInputCmdArgs args) => OnKey(args, ContentKeyFunctions.TryPullObject),
                    ignoreUp: false, outsidePrediction: true))
            .BindBefore(ContentKeyFunctions.EditorFlipObject,
                new PointerInputCmdHandler(OnConfigure, outsidePrediction: true),
                typeof(ConstructionSystem))
            .Register<MapperRcdBrushSystem>();

        _overlays.AddOverlay(new MapperRcdShapeOverlay(this));
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<MapperRcdBrushSystem>();
        _overlays.RemoveOverlay<MapperRcdShapeOverlay>();
    }

    /// <summary>
    /// Drops the current stroke without sending what has not been sent yet.
    /// </summary>
    public void CancelStroke()
    {
        _active = false;
        _visited.Clear();
        _pending.Clear();
        _shape.Clear();
    }

    /// <summary>
    /// The tiles of the line or area being dragged, for the preview overlay.
    /// </summary>
    public bool TryGetShape(out EntityUid grid, out List<Vector2i> cells)
    {
        grid = _grid;
        cells = _shape;
        return _active && _kind != StrokeKind.Brush && _shape.Count > 0;
    }

    private bool OnKey(in PointerInputCmdHandler.PointerInputCmdArgs args, BoundKeyFunction key)
    {
        switch (args.State)
        {
            case BoundKeyState.Up:
                return EndStroke(key);

            case BoundKeyState.Down:
                return BeginStroke(args, key);

            default:
                return false;
        }
    }

    private bool OnConfigure(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        if (!TryGetBrush(out _, out var tool, out _))
            return false;

        if (args.Coordinates.IsValid(EntityManager) && _rcd.TryGetMapGridData(args.Coordinates, out var gridData))
        {
            var world = _transform.ToMapCoordinates(args.Coordinates).Position;
            var cell = _map.WorldToTile(gridData.Value.GridUid, gridData.Value.Component, world);
            NetEntity? target = args.EntityUid.IsValid() ? GetNetEntity(args.EntityUid) : null;

            RaiseNetworkEvent(new MapperRcdConfigureEvent(GetNetEntity(tool), GetNetEntity(gridData.Value.GridUid), cell, target));
        }

        return true;
    }

    private bool EndStroke(BoundKeyFunction key)
    {
        if (!_active || _key != key)
            return false;

        if (_kind != StrokeKind.Brush && TryComp(_grid, out MapGridComponent? grid))
        {
            var mouse = _eye.PixelToMap(_input.MouseScreenPosition);
            if (mouse.MapId == Transform(_grid).MapID)
                BuildShape(_map.WorldToTile(_grid, grid, mouse.Position));

            _pipeLayer = ReadPipeLayer(_entryId);
            _pending.AddRange(_shape);
        }

        Flush();
        CancelStroke();
        return true;
    }

    private bool BeginStroke(in PointerInputCmdHandler.PointerInputCmdArgs args, BoundKeyFunction key)
    {
        CancelStroke();

        if (!TryGetBrush(out var player, out var tool, out var selected))
            return false;

        if (!args.Coordinates.IsValid(EntityManager) ||
            !_rcd.TryGetMapGridData(args.Coordinates, out var gridData))
        {
            _popup.PopupClient(Loc.GetString("mapper-rcd-no-grid"), tool, player);
            return true;
        }

        _active = true;
        _key = key;
        _tool = tool;
        _entryId = selected;
        _grid = gridData.Value.GridUid;
        _pipeLayer = ReadPipeLayer(selected);

        var world = _transform.ToMapCoordinates(args.Coordinates).Position;
        _start = _last = _map.WorldToTile(_grid, gridData.Value.Component, world);

        if (key == ContentKeyFunctions.TryPullObject)
            _kind = StrokeKind.Area;
        else if (key == ContentKeyFunctions.ExamineEntity)
            _kind = StrokeKind.Line;
        else
            _kind = StrokeKind.Brush;

        if (_kind == StrokeKind.Brush)
            AddCell(_last);
        else
            BuildShape(_start);

        return true;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (!_active)
            return;

        if (!_clyde.IsFocused ||
            _inputSystem.CmdStates.GetState(_key) != BoundKeyState.Down ||
            !TryGetBrush(out _, out var tool, out var selected) ||
            tool != _tool ||
            selected != _entryId ||
            !TryComp(_grid, out MapGridComponent? grid))
        {
            CancelStroke();
            return;
        }

        var mouse = _eye.PixelToMap(_input.MouseScreenPosition);
        if (mouse.MapId == Transform(_grid).MapID)
        {
            var cell = _map.WorldToTile(_grid, grid, mouse.Position);

            if (_kind == StrokeKind.Brush)
                AdvanceTo(cell);
            else if (cell != _last)
                BuildShape(cell);
        }

        if (_kind == StrokeKind.Brush && _timing.CurTick > _lastFlushTick)
            Flush();
    }

    private bool TryGetBrush(out EntityUid player, out EntityUid tool, out string selected)
    {
        player = default;
        tool = default;
        selected = string.Empty;

        if (_player.LocalEntity is not { } local ||
            !TryComp(local, out HandsComponent? hands) ||
            hands.ActiveHand?.HeldEntity is not { } held ||
            !TryComp(held, out MapperRcdComponent? rcd) ||
            rcd.SelectedEntry is not { } chosen ||
            !_catalog.TryGetEntry(chosen, out _))
        {
            return false;
        }

        player = local;
        tool = held;
        selected = chosen;
        return true;
    }

    private AtmosPipeLayer ReadPipeLayer(string selected)
    {
        // The pipe placement ghost swaps the placed prototype for the layer under the cursor.
        if (!_catalog.TryGetEntry(selected, out var entry) ||
            entry.Slot != MapperRcdSlot.Pipe ||
            _placement.CurrentPermission?.EntityType is not { } id ||
            !_protos.TryIndex<EntityPrototype>(id, out var proto) ||
            !proto.TryGetComponent<AtmosPipeLayersComponent>(out var layers, EntityManager.ComponentFactory))
        {
            return AtmosPipeLayer.Primary;
        }

        return layers.CurrentPipeLayer;
    }

    /// <summary>
    /// Recomputes the line or area from the pressed tile to <paramref name="end"/>. Cells are ordered so that
    /// each one touches an earlier one, which matters for floors that grow the grid.
    /// </summary>
    private void BuildShape(Vector2i end)
    {
        _shape.Clear();
        _last = end;

        if (_kind == StrokeKind.Line)
        {
            _shape.Add(_start);
            AppendLine(_start, end, _shape, MaxShapeCells);
            return;
        }

        var sx = _start.X <= end.X ? 1 : -1;
        var sy = _start.Y <= end.Y ? 1 : -1;
        var width = Math.Abs(end.X - _start.X) + 1;
        var height = Math.Abs(end.Y - _start.Y) + 1;

        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                if (_shape.Count >= MaxShapeCells)
                    return;

                _shape.Add(new Vector2i(_start.X + i * sx, _start.Y + j * sy));
            }
        }
    }

    private void AdvanceTo(Vector2i target)
    {
        var line = new List<Vector2i>();
        AppendLine(_last, target, line, MaxStrokeCells);

        foreach (var cell in line)
        {
            AddCell(cell);
        }

        _last = target;
    }

    /// <summary>
    /// Appends the tiles after <paramref name="from"/> up to and including <paramref name="to"/>.
    /// </summary>
    private static void AppendLine(Vector2i from, Vector2i to, List<Vector2i> cells, int limit)
    {
        var x = from.X;
        var y = from.Y;
        var dx = Math.Abs(to.X - x);
        var dy = -Math.Abs(to.Y - y);
        var sx = x < to.X ? 1 : -1;
        var sy = y < to.Y ? 1 : -1;
        var err = dx + dy;

        while ((x != to.X || y != to.Y) && cells.Count < limit)
        {
            var e2 = 2 * err;

            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }

            cells.Add(new Vector2i(x, y));
        }
    }

    private void AddCell(Vector2i cell)
    {
        if (_visited.Count < MaxStrokeCells && _visited.Add(cell))
            _pending.Add(cell);
    }

    private void Flush()
    {
        _lastFlushTick = _timing.CurTick;

        if (_pending.Count == 0)
            return;

        var tool = GetNetEntity(_tool);
        var grid = GetNetEntity(_grid);
        var direction = _placement.Direction;

        for (var i = 0; i < _pending.Count; i += MapperRcdStrokeEvent.MaxCells)
        {
            var batch = new List<Vector2i>(MapperRcdStrokeEvent.MaxCells);
            var end = Math.Min(i + MapperRcdStrokeEvent.MaxCells, _pending.Count);

            for (var j = i; j < end; j++)
            {
                batch.Add(_pending[j]);
            }

            RaiseNetworkEvent(new MapperRcdStrokeEvent(tool, grid, _entryId, direction, _pipeLayer, batch));
        }

        _pending.Clear();
    }
}
