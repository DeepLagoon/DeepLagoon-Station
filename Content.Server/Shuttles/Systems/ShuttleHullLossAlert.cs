using Content.Shared.Shuttles.BUIStates;

namespace Content.Server.Shuttles.Systems;

/// <summary>Coalesces new losses; unchanged missing objects never repeat an alarm.</summary>
public sealed class ShuttleHullLossAlert
{
    private ShuttleHullState? _previous;
    private TimeSpan _nextAlert;
    public int Thrusters { get; private set; }
    public int Weapons { get; private set; }
    public int Structures { get; private set; }
    public int Tiles { get; private set; }
    public int Total => Thrusters + Weapons + Structures + Tiles;
    public int DamagePercent { get; private set; }

    public void ObserveNewLosses(ShuttleHullState state, ShuttleHullState losses)
    {
        if (_previous != null)
        {
            Thrusters += losses.MissingThrusters;
            Weapons += losses.MissingWeapons;
            Structures += losses.MissingStructures;
            Tiles += losses.MissingTiles;
        }
        var expected = (long) state.ExpectedThrusters + state.ExpectedWeapons + state.ExpectedStructures + state.ExpectedTiles;
        var missing = (long) state.MissingThrusters + state.MissingWeapons + state.MissingStructures + state.MissingTiles;
        DamagePercent = expected > 0 ? (int) Math.Clamp((missing * 100 + expected - 1) / expected, 0, 100) : 0;
        if (expected > 0 && missing == 0)
        {
            Thrusters = 0;
            Weapons = 0;
            Structures = 0;
            Tiles = 0;
        }
        _previous = state;
    }

    public bool Ready(TimeSpan now) => Total > 0 && now >= _nextAlert;

    public void Acknowledge(TimeSpan now)
    {
        Thrusters = 0;
        Weapons = 0;
        Structures = 0;
        Tiles = 0;
        _nextAlert = now + TimeSpan.FromSeconds(6);
    }
}
