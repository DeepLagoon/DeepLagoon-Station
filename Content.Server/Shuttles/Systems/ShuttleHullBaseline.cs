using Content.Shared.Shuttles.BUIStates;
using Robust.Shared.Maths;

namespace Content.Server.Shuttles.Systems;

/// <summary>
/// Retains the design footprint rather than drawing real holes. A replacement of the same category at
/// the same tile repairs a missing slot; additions expand the design without resetting earlier losses.
/// </summary>
public sealed class ShuttleHullBaseline
{
    public const int MaxResolution = 64;
    public const byte Floor = 1;
    public const byte Thruster = 4;
    public const byte Weapon = 8;
    public const byte Structure = 16;

    private readonly Dictionary<(Vector2i Tile, byte Kind), int> _expected = new();
    private readonly Dictionary<(Vector2i Tile, byte Kind), int> _previousMissing = new();
    private readonly Dictionary<(Vector2i Tile, byte Kind), int> _newlyMissing = new();
    public ShuttleHullState NewLosses { get; private set; } = new();
    public int ExpectedAt(Vector2i tile, byte kind) => _expected.GetValueOrDefault((tile, kind));
    public int NewlyMissingAt(Vector2i tile, byte kind) => _newlyMissing.GetValueOrDefault((tile, kind));

    public void Record(Vector2i tile, byte kind, int count = 1)
    {
        var key = (tile, kind);
        _expected[key] = Math.Max(_expected.GetValueOrDefault(key), count);
    }

    public ShuttleHullState Build(IReadOnlyDictionary<(Vector2i Tile, byte Kind), int> current)
    {
        foreach (var (key, count) in current)
            Record(key.Tile, key.Kind, count);

        var state = new ShuttleHullState();
        NewLosses = new ShuttleHullState();
        _newlyMissing.Clear();
        if (_expected.Count == 0)
            return state;

        var min = new Vector2i(int.MaxValue, int.MaxValue);
        var max = new Vector2i(int.MinValue, int.MinValue);
        foreach (var key in _expected.Keys)
        {
            min = Vector2i.ComponentMin(min, key.Tile);
            max = Vector2i.ComponentMax(max, key.Tile);
        }

        // One common bucket size preserves aspect ratio even for exceptionally large grids.
        var spanX = (long) max.X - min.X + 1;
        var spanY = (long) max.Y - min.Y + 1;
        var bucket = Math.Max(1L, (Math.Max(spanX, spanY) + MaxResolution - 1) / MaxResolution);
        state.OriginX = min.X;
        state.OriginY = min.Y;
        state.CellSize = (int) bucket;
        state.Width = (int) ((spanX + bucket - 1) / bucket);
        state.Height = (int) ((spanY + bucket - 1) / bucket);
        state.Cells = new byte[state.Width * state.Height];

        foreach (var (key, expected) in _expected)
        {
            var missing = Math.Max(0, expected - current.GetValueOrDefault(key));
            var newlyMissing = Math.Max(0, missing - _previousMissing.GetValueOrDefault(key));
            if (newlyMissing > 0)
                _newlyMissing[key] = newlyMissing;
            _previousMissing[key] = missing;
            var x = (int) (((long) key.Tile.X - min.X) / bucket);
            var y = (int) (((long) key.Tile.Y - min.Y) / bucket);
            state.Cells[y * state.Width + x] |= (byte) (1 | key.Kind | (missing > 0 ? 2 : 0));
            switch (key.Kind)
            {
                case Floor:
                    state.ExpectedTiles += expected;
                    state.MissingTiles += missing;
                    NewLosses.MissingTiles += newlyMissing;
                    break;
                case Thruster:
                    state.ExpectedThrusters += expected;
                    state.MissingThrusters += missing;
                    NewLosses.MissingThrusters += newlyMissing;
                    break;
                case Weapon:
                    state.ExpectedWeapons += expected;
                    state.MissingWeapons += missing;
                    NewLosses.MissingWeapons += newlyMissing;
                    break;
                case Structure:
                    state.ExpectedStructures += expected;
                    state.MissingStructures += missing;
                    NewLosses.MissingStructures += newlyMissing;
                    break;
            }
        }

        var total = (long) state.ExpectedTiles + state.ExpectedStructures + state.ExpectedThrusters + state.ExpectedWeapons;
        var missingTotal = (long) state.MissingTiles + state.MissingStructures + state.MissingThrusters + state.MissingWeapons;
        state.IntegrityFraction = total > 0 ? (float) (total - missingTotal) / total : 1f;
        return state;
    }
}
