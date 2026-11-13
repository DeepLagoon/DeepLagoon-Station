using System.Numerics;
using Content.Shared.Exchanger;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._NF.Construction;

public sealed partial class ExchangeTrailSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    public void Show(EntityUid from, EntityUid to, TimeSpan? lifetime)
    {
        if (TerminatingOrDeleted(from) || TerminatingOrDeleted(to))
            return;

        var marker = Spawn("PartExchangeTrail", new EntityCoordinates(from, Vector2.Zero));
        var trail = Comp<ExchangeTrailComponent>(marker);
        trail.Target = to;
        trail.EndsAt = lifetime is { } duration ? _timing.CurTime + duration : null;
        Dirty(marker, trail);
    }

    public override void Update(float frameTime)
    {
        var time = _timing.CurTime;
        var trails = EntityQueryEnumerator<ExchangeTrailComponent>();
        while (trails.MoveNext(out var uid, out var trail))
        {
            if (trail.EndsAt is { } end && time >= end || TerminatingOrDeleted(trail.Target))
                QueueDel(uid);
        }
    }
}
