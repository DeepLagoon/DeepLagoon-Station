// Adapted from Einstein Engines.
using Content.Shared.Forensics.Systems;
using Content.Shared.Forensics;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Client.Player;

namespace Content.Client.Forensics;

public sealed class ScentTrackerSystem : SharedScentTrackerSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Client-only visual effects must not be searched/spawned again for replayed ticks.
        if (!_timing.IsFirstTimePredicted
            || !TryComp<ScentTrackerComponent>(_playerManager.LocalEntity, out var tracker)
            || tracker.Scent == string.Empty)
            return;

        var query = AllEntityQuery<ScentTrailComponent>();
        while (query.MoveNext(out var uid, out var comp))
            if (tracker.Scent == comp.Scent
                && _timing.CurTime > comp.TargetTime)
            {
                comp.TargetTime = _timing.CurTime + TimeSpan.FromSeconds(1.0f);
                Spawn("ScentTrackEffect", _transform.GetMapCoordinates(uid).Offset(_random.NextVector2(0.25f)));
            }
    }
}
