using Content.Server._Crescent.ShipShields;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.FireControl;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem
{
    private readonly HashSet<EntityUid> _pendingShieldGrids = new();
    private TimeSpan _nextShieldUiUpdate;
    [Dependency] private IGameTiming _shieldTiming = default!;
    [Dependency] private ShipShieldsSystem _shipShields = default!;

    private void InitializeShieldUi() => SubscribeLocalEvent<MapGridComponent, ShieldStatusDirtyEvent>(OnShieldStateChanged);

    private void OnShieldStateChanged(Entity<MapGridComponent> grid, ref ShieldStatusDirtyEvent args)
    {
        _pendingShieldGrids.Add(grid);
    }

    private void ProcessShieldUiUpdates()
    {
        if (_pendingShieldGrids.Count == 0 || _shieldTiming.CurTime < _nextShieldUiUpdate)
            return;
        var messages = new Dictionary<EntityUid, ShieldReadoutMessage>();
        SendShieldReadouts<ShuttleConsoleComponent>(ShuttleConsoleUiKey.Key, messages);
        SendShieldReadouts<FireControlConsoleComponent>(FireControlConsoleUiKey.Key, messages);
        _pendingShieldGrids.Clear();
        _nextShieldUiUpdate = _shieldTiming.CurTime + TimeSpan.FromMilliseconds(250);
    }

    private void SendShieldReadouts<T>(Enum key, Dictionary<EntityUid, ShieldReadoutMessage> messages)
        where T : IComponent
    {
        var consoles = EntityQueryEnumerator<T, TransformComponent>();
        while (consoles.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid is not { } grid || !_pendingShieldGrids.Contains(grid) || !_ui.IsUiOpen(uid, key))
                continue;

            if (!messages.TryGetValue(grid, out var message))
                messages.Add(grid, message = new ShieldReadoutMessage(_shipShields.GetShieldState(grid)));
            _ui.ServerSendUiMessage(uid, key, message);
        }
    }
}
