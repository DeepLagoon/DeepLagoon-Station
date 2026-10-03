using System.Numerics;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

/// <summary>Uniform world-space buckets. Rays visit only their crossed buckets; exact box tests remain unchanged.</summary>
internal sealed class AmbientOcclusionVisibilityIndex
{
    private const float CellSize = 2;
    private readonly Dictionary<Vector2i, List<int>> _buckets = new();
    private readonly Stack<List<int>> _pool = new();
    private readonly List<(EntityUid Uid, Box2 Bounds)> _blockers = new();
    private readonly Dictionary<EntityUid, Box2> _bounds = new();
    private readonly List<int> _large = new();
    private int[] _seen = Array.Empty<int>();
    private int _generation;
    private bool _valid;
    public int Tests { get; private set; }

    public bool EnsureCurrent(List<(EntityUid Uid, Box2 Bounds)> blockers)
    {
        if (_valid && _blockers.Count == blockers.Count)
        {
            var same = true;
            foreach (var (uid, box) in blockers)
                if (!_bounds.TryGetValue(uid, out var old) || !old.Equals(box)) { same = false; break; }
            if (same) return false;
        }
        Rebuild(blockers);
        return true;
    }

    public void Rebuild(List<(EntityUid Uid, Box2 Bounds)> blockers)
    {
        _valid = true;
        foreach (var bucket in _buckets.Values) { bucket.Clear(); _pool.Push(bucket); }
        _buckets.Clear();
        _blockers.Clear();
        _blockers.AddRange(blockers);
        _bounds.Clear();
        foreach (var (uid, box) in blockers) _bounds[uid] = box;
        _large.Clear();
        if (_seen.Length < blockers.Count) Array.Resize(ref _seen, blockers.Count);
        for (var i = 0; i < blockers.Count; i++)
        {
            // Include both sides of exact cell boundaries and the segment test's near-zero tolerance.
            var bounds = blockers[i].Bounds.Enlarged(0.00002f);
            var min = (bounds.BottomLeft / CellSize).Floored();
            var max = (bounds.TopRight / CellSize).Floored();
            if ((long) (max.X - min.X + 1) * (max.Y - min.Y + 1) > 4096)
            {
                _large.Add(i);
                continue;
            }
            for (var y = min.Y; y <= max.Y; y++)
            for (var x = min.X; x <= max.X; x++)
            {
                var cell = new Vector2i(x, y);
                if (!_buckets.TryGetValue(cell, out var bucket))
                    _buckets.Add(cell, bucket = _pool.Count > 0 ? _pool.Pop() : new List<int>());
                bucket.Add(i);
            }
        }
    }

    public bool IsBlocked(Vector2 start, Vector2 end, EntityUid ignored)
    {
        Tests = 0;
        if (_generation == int.MaxValue) { Array.Clear(_seen); _generation = 0; }
        _generation++;
        var cell = (start / CellSize).Floored();
        var last = (end / CellSize).Floored();
        var distance = Math.Abs((long) last.X - cell.X) + Math.Abs((long) last.Y - cell.Y);
        if (distance > 1024)
        {
            for (var i = 0; i < _blockers.Count; i++) if (Hit(i)) return true;
            return false;
        }
        foreach (var i in _large) if (Hit(i)) return true;
        var direction = end - start;
        var sx = Math.Sign(direction.X);
        var sy = Math.Sign(direction.Y);
        var dx = sx == 0 ? float.PositiveInfinity : CellSize / Math.Abs(direction.X);
        var dy = sy == 0 ? float.PositiveInfinity : CellSize / Math.Abs(direction.Y);
        var tx = sx == 0 ? float.PositiveInfinity : ((cell.X + (sx > 0 ? 1 : 0)) * CellSize - start.X) / direction.X;
        var ty = sy == 0 ? float.PositiveInfinity : ((cell.Y + (sy > 0 ? 1 : 0)) * CellSize - start.Y) / direction.Y;
        for (long step = 0; step <= distance + 2; step++)
        {
            if (CellHit(cell)) return true;
            if (cell == last) return false;
            if (tx == ty)
            {
                // Supercover at a corner: test both neighboring cells before advancing diagonally.
                if (CellHit(cell + new Vector2i(sx, 0)) || CellHit(cell + new Vector2i(0, sy))) return true;
                cell += new Vector2i(sx, sy); tx += dx; ty += dy;
            }
            else if (tx < ty) { cell += new Vector2i(sx, 0); tx += dx; }
            else { cell += new Vector2i(0, sy); ty += dy; }
        }
        // Numerical edge cases fail safely through the original exact scan.
        for (var i = 0; i < _blockers.Count; i++) if (Hit(i)) return true;
        return false;

        bool CellHit(Vector2i position)
        {
            if (!_buckets.TryGetValue(position, out var bucket)) return false;
            foreach (var i in bucket) if (Hit(i)) return true;
            return false;
        }

        bool Hit(int index)
        {
            if (_seen[index] == _generation) return false;
            _seen[index] = _generation;
            var blocker = _blockers[index];
            if (blocker.Uid == ignored) return false;
            Tests++;
            return AmbientOcclusionSystem.ContactOverlay.SegmentIntersects(start, end, blocker.Bounds);
        }
    }
}
