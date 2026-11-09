using Content.Shared.Bed.Sleep;
using Content.Shared.Rejuvenate;

namespace Content.Server.Administration.Systems;

public sealed partial class RejuvenateSystem : EntitySystem
{
    [Dependency] private SleepingSystem _sleeping = default!;

    public void PerformRejuvenate(EntityUid target)
    {
        RaiseLocalEvent(target, new RejuvenateEvent());
        _sleeping.TryWaking(target, force: true);
    }
}
