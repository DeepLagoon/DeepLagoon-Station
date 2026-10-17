using Content.Shared._DeepLagoon.Performance;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Performance;

public sealed class ServerFpsSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    private TimeSpan _nextUpdate;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.RealTime < _nextUpdate)
            return;

        _nextUpdate = _timing.RealTime + TimeSpan.FromSeconds(1);
        // Use the same measured FPS as the server console, rather than the configured tick rate.
        RaiseNetworkEvent(new ServerFpsEvent(_timing.FramesPerSecondAvg), Filter.Empty().AddAllPlayers(), recordReplay: false);
    }
}
