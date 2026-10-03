using System.Linq;
using System.Numerics;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

internal readonly record struct AmbientOcclusionSceneKey(Vector2i Min, Vector2i Max, Matrix3x2 Inverse,
    Vector2 Eye, bool Fov, float Intensity, bool Entities);

/// <summary>Exact current-state comparison; no hashes, timers, or stale visibility results.</summary>
internal sealed class AmbientOcclusionSceneCache
{
    public int LastMiss { get; private set; }
    private bool _valid;
    private AmbientOcclusionSceneKey _key;
    private readonly List<(EntityUid Uid, EntityUid Grid, Vector2 Position, Vector2 Size, float Alpha)> _objects = new();
    private readonly List<(EntityUid Uid, Box2 Bounds)> _blockers = new();
    private readonly List<(Vector2i Position, int Walls)> _tiles = new();

    public bool Matches(AmbientOcclusionSceneKey key,
        List<(EntityUid Uid, EntityUid Grid, Vector2 Position, Vector2 Size, float Alpha)> objects,
        List<(EntityUid Uid, Box2 Bounds)> blockers, List<(Vector2i Position, int Walls)> tiles)
    {
        // Record the first differing group using the comparisons already needed for validation.
        LastMiss = !_valid || _key != key ? 1 : !_objects.SequenceEqual(objects) ? 2 :
            !_blockers.SequenceEqual(blockers) ? 3 : !_tiles.SequenceEqual(tiles) ? 4 : 0;
        return LastMiss == 0;
    }

    public void Store(AmbientOcclusionSceneKey key,
        List<(EntityUid Uid, EntityUid Grid, Vector2 Position, Vector2 Size, float Alpha)> objects,
        List<(EntityUid Uid, Box2 Bounds)> blockers, List<(Vector2i Position, int Walls)> tiles)
    {
        _key = key;
        _objects.Clear(); _objects.AddRange(objects);
        _blockers.Clear(); _blockers.AddRange(blockers);
        _tiles.Clear(); _tiles.AddRange(tiles);
        _valid = true;
    }
}
