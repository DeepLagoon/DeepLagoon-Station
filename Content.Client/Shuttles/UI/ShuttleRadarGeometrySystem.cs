using System.Numerics;
using Content.Shared._Mono.GridEdgeMarker;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.Shuttles.UI;

[RegisterComponent]
public sealed partial class ShuttleRadarCacheComponent : Component;

/// <summary>Shares grid-local radar meshes across consoles. Drawing never rebuilds an entire mesh.</summary>
public sealed partial class ShuttleRadarGeometrySystem : EntitySystem
{
    private const int RegionSize = 16;
    internal const int OverviewStep = 2;
    private const int SampleWidth = RegionSize + 2 * OverviewStep;
    private const int BuildsPerFrame = 8;
    private const int MaxVertices = 2_000_000;
    private static readonly TimeSpan UpdateDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(45);
    private static readonly Vector2i[] Neighbors = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];

    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;

    private readonly Dictionary<EntityUid, GridMesh> _grids = new();
    private readonly Dictionary<int, Shape> _shapes = new();
    private readonly LinkedList<Region> _lru = new();
    private readonly List<EntityUid> _expired = new();
    private readonly List<Vector2i> _empty = new();
    private readonly Shape?[] _samples = new Shape?[SampleWidth * SampleWidth];
    private readonly bool[] _full = new bool[RegionSize * RegionSize];
    private readonly List<Vector2> _fill = new();
    private readonly List<Vector2> _edges = new();
    private readonly List<AxisEdge> _axis = new();
    private readonly HashSet<Entity<GridEdgeMarkerComponent>> _markers = new();
    private int _buildsLeft = BuildsPerFrame;
    private int _vertices;
    private ulong _frame;
    private TimeSpan _nextPrune;

    public override void Initialize()
    {
        SubscribeLocalEvent<ShuttleRadarCacheComponent, TileChangedEvent>(OnTiles);
        SubscribeLocalEvent<ShuttleRadarCacheComponent, ComponentShutdown>(OnGridShutdown);
        SubscribeLocalEvent<GridEdgeMarkerComponent, ComponentStartup>(OnMarkerStartup);
        SubscribeLocalEvent<GridEdgeMarkerComponent, ComponentShutdown>(OnMarkerShutdown);
        SubscribeLocalEvent<GridEdgeMarkerComponent, MoveEvent>(OnMarkerMoved);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public override void FrameUpdate(float frameTime)
    {
        _frame++;
        _buildsLeft = BuildsPerFrame;
        var now = _timing.RealTime;
        if (now < _nextPrune)
            return;

        _nextPrune = now + TimeSpan.FromSeconds(5);
        _expired.Clear();
        foreach (var (uid, mesh) in _grids)
        {
            if (now - mesh.LastUse >= IdleTime)
                _expired.Add(uid);
        }

        foreach (var uid in _expired)
            RemComp<ShuttleRadarCacheComponent>(uid);
    }

    internal GridMesh GetVisibleMesh(Entity<MapGridComponent> grid, Box2 bounds, bool overview)
    {
        var now = _timing.RealTime;
        if (!_grids.TryGetValue(grid, out var mesh))
        {
            mesh = new GridMesh();
            _grids.Add(grid, mesh);
            EnsureComp<ShuttleRadarCacheComponent>(grid);
            IndexTiles(grid, mesh);
        }
        else if (mesh.LastTileTick != grid.Comp.LastTileModifiedTick)
        {
            if (!mesh.ReceivedTileEvents)
                IndexTiles(grid, mesh);

            mesh.LastTileTick = grid.Comp.LastTileModifiedTick;
            mesh.ReceivedTileEvents = false;
        }

        mesh.LastUse = now;
        var worldSize = RegionSize * grid.Comp.TileSize;
        _empty.Clear();
        foreach (var (key, region) in mesh.Regions)
        {
            if (!RegionBounds(key, worldSize).Intersects(bounds))
                continue;

            var due = overview
                ? region.OverviewDirty && now >= region.NextOverviewBuild
                : region.Dirty && now >= region.NextDetailBuild;
            if (_buildsLeft > 0 && due)
            {
                _buildsLeft--;
                BuildRegion(grid, key, region, overview);
                if (overview)
                    region.NextOverviewBuild = now + UpdateDelay;
                else
                    region.NextDetailBuild = now + UpdateDelay;
                if (region.IsEmpty)
                {
                    _empty.Add(key);
                    continue;
                }
            }

            if (region.Node != null && region.LastUsedFrame != _frame)
            {
                _lru.Remove(region.Node);
                _lru.AddLast(region.Node);
                region.LastUsedFrame = _frame;
            }
        }

        foreach (var key in _empty)
        {
            Evict(mesh.Regions[key]);
            mesh.Regions.Remove(key);
        }

        if (mesh.MarkersDirty)
            UpdateMarkers(grid, mesh);

        return mesh;
    }

    internal static Box2 RegionBounds(Vector2i key, int worldSize)
        => Box2.FromDimensions((Vector2) key * worldSize, new Vector2(worldSize));

    internal static int RegionWorldSize(MapGridComponent grid) => RegionSize * grid.TileSize;

    private void IndexTiles(Entity<MapGridComponent> grid, GridMesh mesh)
    {
        foreach (var region in mesh.Regions.Values)
            region.Invalidate();

        var tiles = _map.GetAllTiles(grid, grid.Comp);
        while (tiles.MoveNext(out var tile))
            mesh.GetRegion(SharedMapSystem.GetChunkIndices(tile.Value.GridIndices, RegionSize));

        mesh.LastTileTick = grid.Comp.LastTileModifiedTick;
    }

    private void OnTiles(Entity<ShuttleRadarCacheComponent> grid, ref TileChangedEvent args)
    {
        if (!_grids.TryGetValue(grid, out var mesh))
            return;

        mesh.ReceivedTileEvents = true;
        foreach (var change in args.Changes)
        {
            if (change.NewTile.TypeId == change.OldTile.TypeId)
                continue;

            var key = SharedMapSystem.GetChunkIndices(change.GridIndices, RegionSize);
            if (!change.NewTile.IsEmpty || mesh.Regions.ContainsKey(key))
                mesh.GetRegion(key).Invalidate();

            var local = change.GridIndices - key * RegionSize;
            for (var side = 0; side < Neighbors.Length; side++)
            {
                var distance = side switch
                {
                    0 => local.X,
                    1 => RegionSize - 1 - local.X,
                    2 => local.Y,
                    _ => RegionSize - 1 - local.Y,
                };

                if (distance < OverviewStep && mesh.Regions.TryGetValue(key + Neighbors[side], out var adjacent))
                    adjacent.Invalidate();
            }
        }
    }

    private void OnGridShutdown(Entity<ShuttleRadarCacheComponent> grid, ref ComponentShutdown args) => RemoveGrid(grid);
    private void OnMarkerStartup(Entity<GridEdgeMarkerComponent> marker, ref ComponentStartup args) => DirtyMarkers(Transform(marker).GridUid);
    private void OnMarkerShutdown(Entity<GridEdgeMarkerComponent> marker, ref ComponentShutdown args) => DirtyMarkers(Transform(marker).GridUid);

    private void OnMarkerMoved(Entity<GridEdgeMarkerComponent> marker, ref MoveEvent args)
    {
        DirtyMarkers(args.OldPosition.EntityId);
        DirtyMarkers(Transform(marker).GridUid);
    }

    private void DirtyMarkers(EntityUid? grid)
    {
        if (grid is { } uid && _grids.TryGetValue(uid, out var mesh))
            mesh.MarkersDirty = true;
    }

    private void UpdateMarkers(Entity<MapGridComponent> grid, GridMesh mesh)
    {
        _markers.Clear();
        _lookup.GetLocalEntitiesIntersecting(grid, grid.Comp.LocalAABB, _markers);
        mesh.Markers.Clear();
        foreach (var marker in _markers)
        {
            var transform = Transform(marker);
            if (transform.ParentUid != grid.Owner)
                continue;

            mesh.Markers.Add(transform.LocalPosition + transform.LocalRotation.RotateVec(marker.Comp.Begin) * grid.Comp.TileSize);
            mesh.Markers.Add(transform.LocalPosition + transform.LocalRotation.RotateVec(marker.Comp.End) * grid.Comp.TileSize);
        }

        mesh.MarkersDirty = false;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<ContentTileDefinition>())
            return;

        _shapes.Clear();
        foreach (var mesh in _grids.Values)
        foreach (var region in mesh.Regions.Values)
            region.Invalidate();
    }

    private void RemoveGrid(EntityUid grid)
    {
        if (!_grids.Remove(grid, out var mesh))
            return;

        foreach (var region in mesh.Regions.Values)
            Evict(region);
    }

    private void Evict(Region region)
    {
        _vertices -= region.VertexCount;
        region.Detail = region.Overview = null;
        region.Invalidate();
        if (region.Node == null)
            return;

        _lru.Remove(region.Node);
        region.Node = null;
    }

    private void BuildRegion(Entity<MapGridComponent> grid, Vector2i key, Region region, bool overview)
    {
        var start = key * RegionSize;
        var tileSize = grid.Comp.TileSize;
        var padding = overview ? OverviewStep : 1;
        for (var y = -padding; y < RegionSize + padding; y++)
        for (var x = -padding; x < RegionSize + padding; x++)
        {
            var tile = start + new Vector2i(x, y);
            _samples[SampleIndex(x, y)] = _map.TryGetTileRef(grid, grid.Comp, tile, out var tileRef) && !tileRef.Tile.IsEmpty
                ? GetShape(tileRef.Tile.TypeId)
                : null;
        }

        _fill.Clear();
        _edges.Clear();
        _axis.Clear();
        if (overview)
            BuildOverview((Vector2) start, tileSize);
        else
            BuildDetail((Vector2) start, tileSize);

        MergeAxisEdges();
        region.IsEmpty = _fill.Count == 0;
        if (region.IsEmpty)
            return;

        var previous = region.VertexCount;
        var geometry = new Geometry(_fill.ToArray(), _edges.ToArray());
        if (overview)
        {
            region.Overview = geometry;
            region.OverviewDirty = false;
        }
        else
        {
            region.Detail = geometry;
            region.Dirty = false;
        }

        _vertices += region.VertexCount - previous;
        if (region.Node == null)
            region.Node = _lru.AddLast(region);

        while (_vertices > MaxVertices && _lru.First is { } oldest)
            Evict(oldest.Value);
    }

    private void BuildDetail(Vector2 start, float size)
    {
        for (var y = 0; y < RegionSize; y++)
        for (var x = 0; x < RegionSize; x++)
        {
            var shape = Sample(x, y);
            _full[y * RegionSize + x] = shape?.Square == true;
            if (shape == null)
                continue;

            var corner = (start + new Vector2(x, y)) * size;
            if (!shape.Square)
            {
                var points = shape.Points;
                for (var i = 2; i < points.Count; i++)
                    AddTriangle(corner + points[0] * size, corner + points[i - 1] * size, corner + points[i] * size);
            }

            foreach (var edge in shape.Edges)
            {
                if (edge.Side < 0)
                {
                    AddEdge(corner + edge.A * size, corner + edge.B * size);
                    continue;
                }

                var offset = Neighbors[edge.Side];
                var neighbor = Sample(x + offset.X, y + offset.Y)?.Sides[edge.Side ^ 1];
                AddExposedSide(corner, size, edge.Side, edge.Range, neighbor);
            }
        }

        FillRectangles(start, size, RegionSize);
    }

    // Merge maximal solid rectangles before turning them into triangles.
    private void FillRectangles(Vector2 start, float size, int columns)
    {
        for (var y = 0; y < columns; y++)
        for (var x = 0; x < columns; x++)
        {
            if (!_full[y * columns + x])
                continue;

            var right = x + 1;
            while (right < columns && _full[y * columns + right])
                right++;

            var top = y + 1;
            while (top < columns && SolidRow(top, x, right, columns))
                top++;

            for (var row = y; row < top; row++)
                Array.Clear(_full, row * columns + x, right - x);

            AddRectangle((start + new Vector2(x, y)) * size, (start + new Vector2(right, top)) * size);
        }
    }

    private bool SolidRow(int y, int left, int right, int columns)
    {
        for (var x = left; x < right; x++)
        {
            if (!_full[y * columns + x])
                return false;
        }

        return true;
    }

    private void BuildOverview(Vector2 start, float size)
    {
        var blockSize = OverviewStep * size;
        const int blocks = RegionSize / OverviewStep;
        for (var y = 0; y < RegionSize; y += OverviewStep)
        for (var x = 0; x < RegionSize; x += OverviewStep)
        {
            var occupied = OccupiedBlock(x, y);
            _full[y / OverviewStep * blocks + x / OverviewStep] = occupied;
            if (!occupied)
                continue;

            var corner = (start + new Vector2(x, y)) * size;
            for (var side = 0; side < Neighbors.Length; side++)
            {
                var offset = Neighbors[side] * OverviewStep;
                if (!OccupiedBlock(x + offset.X, y + offset.Y))
                    AddSide(corner, blockSize, side, 0, 1);
            }
        }

        FillRectangles(start / OverviewStep, blockSize, blocks);
    }

    private bool OccupiedBlock(int x, int y)
    {
        for (var iy = y; iy < y + OverviewStep; iy++)
        for (var ix = x; ix < x + OverviewStep; ix++)
        {
            if (Sample(ix, iy) != null)
                return true;
        }

        return false;
    }

    private static int SampleIndex(int x, int y) => (y + OverviewStep) * SampleWidth + x + OverviewStep;
    private Shape? Sample(int x, int y) => _samples[SampleIndex(x, y)];

    private Shape GetShape(int id)
    {
        if (_shapes.TryGetValue(id, out var shape))
            return shape;

        var points = ((ContentTileDefinition) _tiles[id]).Vertices;
        var edges = new TileEdge[points.Count];
        var sides = new Interval?[4];
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var side = a.X == b.X && a.X == 0 ? 0 :
                a.X == b.X && a.X == 1 ? 1 :
                a.Y == b.Y && a.Y == 0 ? 2 :
                a.Y == b.Y && a.Y == 1 ? 3 : -1;
            var first = side < 2 ? a.Y : a.X;
            var second = side < 2 ? b.Y : b.X;
            var range = new Interval(Math.Min(first, second), Math.Max(first, second));
            edges[i] = new TileEdge(a, b, side, range);
            if (side >= 0)
                sides[side] = range;
        }

        var square = points.Count == 4 && Array.TrueForAll(sides, side => side == new Interval(0, 1));
        shape = new Shape(points, edges, sides, square);
        _shapes.Add(id, shape);
        return shape;
    }

    private void AddExposedSide(Vector2 corner, float size, int side, Interval edge, Interval? neighbor)
    {
        if (neighbor is not { } other || other.End <= edge.Start || other.Start >= edge.End)
        {
            AddSide(corner, size, side, edge.Start, edge.End);
            return;
        }

        AddSide(corner, size, side, edge.Start, other.Start);
        AddSide(corner, size, side, other.End, edge.End);
    }

    private void AddSide(Vector2 corner, float size, int side, float first, float last)
    {
        if (last <= first)
            return;

        if (side < 2)
            _axis.Add(new AxisEdge(true, corner.X + side * size, corner.Y + first * size, corner.Y + last * size));
        else
            _axis.Add(new AxisEdge(false, corner.Y + (side - 2) * size, corner.X + first * size, corner.X + last * size));
    }

    private void MergeAxisEdges()
    {
        _axis.Sort(static (a, b) =>
        {
            var result = a.Vertical.CompareTo(b.Vertical);
            if (result != 0)
                return result;
            result = a.Fixed.CompareTo(b.Fixed);
            return result != 0 ? result : a.Start.CompareTo(b.Start);
        });

        for (var i = 0; i < _axis.Count;)
        {
            var edge = _axis[i++];
            while (i < _axis.Count && _axis[i].Vertical == edge.Vertical &&
                   _axis[i].Fixed == edge.Fixed && _axis[i].Start <= edge.End)
            {
                edge = edge with { End = Math.Max(edge.End, _axis[i++].End) };
            }

            AddEdge(edge.Vertical ? new Vector2(edge.Fixed, edge.Start) : new Vector2(edge.Start, edge.Fixed),
                edge.Vertical ? new Vector2(edge.Fixed, edge.End) : new Vector2(edge.End, edge.Fixed));
        }
    }

    private void AddEdge(Vector2 a, Vector2 b)
    {
        if (a == b)
            return;

        _edges.Add(a);
        _edges.Add(b);
    }

    private void AddRectangle(Vector2 min, Vector2 max)
    {
        var right = new Vector2(max.X, min.Y);
        var left = new Vector2(min.X, max.Y);
        AddTriangle(min, right, max);
        AddTriangle(min, max, left);
    }

    private void AddTriangle(Vector2 a, Vector2 b, Vector2 c)
    {
        _fill.Add(a);
        _fill.Add(b);
        _fill.Add(c);
    }

    internal sealed class GridMesh
    {
        public readonly Dictionary<Vector2i, Region> Regions = new();
        public readonly List<Vector2> Markers = new();
        public TimeSpan LastUse;
        public GameTick LastTileTick;
        public bool ReceivedTileEvents;
        public bool MarkersDirty = true;

        public Region GetRegion(Vector2i key)
        {
            if (!Regions.TryGetValue(key, out var region))
                Regions.Add(key, region = new Region());
            return region;
        }
    }

    internal sealed class Region
    {
        public Geometry? Detail;
        public Geometry? Overview;
        public bool Dirty = true;
        public bool OverviewDirty = true;
        public bool IsEmpty;
        public TimeSpan NextDetailBuild;
        public TimeSpan NextOverviewBuild;
        public LinkedListNode<Region>? Node;
        public ulong LastUsedFrame = ulong.MaxValue;
        public int VertexCount => (Detail?.Count ?? 0) + (Overview?.Count ?? 0);

        public void Invalidate() => Dirty = OverviewDirty = true;
    }

    internal sealed record Geometry(Vector2[] Fill, Vector2[] Edges)
    {
        public int Count => Fill.Length + Edges.Length;
    }

    private sealed record Shape(IReadOnlyList<Vector2> Points, TileEdge[] Edges, Interval?[] Sides, bool Square);
    private readonly record struct TileEdge(Vector2 A, Vector2 B, int Side, Interval Range);
    private readonly record struct Interval(float Start, float End);
    private readonly record struct AxisEdge(bool Vertical, float Fixed, float Start, float End);
}
