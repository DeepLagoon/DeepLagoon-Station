using Content.Server.Explosion.Components;
using Content.Shared.Implants.Components;

namespace Content.Server.Explosion.EntitySystems;

public sealed partial class TriggerSystem
{
    public bool ShouldSendRattleAlert(EntityUid patient, RattleComponent implant, string kind)
    {
        var history = EnsureComp<RattleAlertCooldownComponent>(patient);
        var key = (implant.RadioChannel.Id, kind);
        var now = _timing.CurTime;
        if (history.LastSent.TryGetValue(key, out var last) && now - last < implant.RepeatDelay)
            return false;
        history.LastSent[key] = now;
        return true;
    }
}
