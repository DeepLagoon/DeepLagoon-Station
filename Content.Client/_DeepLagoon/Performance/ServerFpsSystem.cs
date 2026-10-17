using Content.Shared._DeepLagoon.Performance;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Performance;

public sealed class ServerFpsSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    private double? _framesPerSecond;
    private TimeSpan _lastUpdate;

    public double? FramesPerSecond => _timing.RealTime - _lastUpdate < TimeSpan.FromSeconds(5)
        ? _framesPerSecond
        : null;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<ServerFpsEvent>(message =>
        {
            _framesPerSecond = message.FramesPerSecond;
            _lastUpdate = _timing.RealTime;
        });
    }
}
