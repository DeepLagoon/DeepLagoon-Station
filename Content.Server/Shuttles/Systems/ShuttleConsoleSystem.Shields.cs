using Content.Server._Crescent.ShipShields;
using Content.Server.Shuttles.Components;
using Content.Shared.Exodus.ShipShields;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Timing;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem
{
    private readonly HashSet<EntityUid> _pendingShieldGrids = new();
    private TimeSpan _nextShieldUiUpdate;
    [Dependency] private IGameTiming _shieldTiming = default!;
    [Dependency] private ShipShieldsSystem _shipShields = default!;

    private void InitializeShieldUi() => SubscribeLocalEvent<ShipShieldStateChangedEvent>(OnShieldStateChanged);

    private void OnShieldStateChanged(ref ShipShieldStateChangedEvent args) => _pendingShieldGrids.Add(args.Grid);

    private void ProcessShieldUiUpdates()
    {
        if (_pendingShieldGrids.Count == 0 || _shieldTiming.CurTime < _nextShieldUiUpdate)
            return;
        var states = new Dictionary<EntityUid, ShipShieldState?>();
        var query = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid is not { } grid || !_pendingShieldGrids.Contains(grid)
                || !_ui.IsUiOpen(uid, ShuttleConsoleUiKey.Key))
                continue;
            if (!states.TryGetValue(grid, out var state))
                states.Add(grid, state = _shipShields.GetShieldState(grid));
            _ui.ServerSendUiMessage(uid, ShuttleConsoleUiKey.Key, new ShuttleShieldStateMessage(state));
        }
        _pendingShieldGrids.Clear();
        _nextShieldUiUpdate = _shieldTiming.CurTime + TimeSpan.FromMilliseconds(250);
    }
}
