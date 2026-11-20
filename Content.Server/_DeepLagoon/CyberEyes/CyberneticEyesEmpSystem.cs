
using Content.Shared._DeepLagoon.CyberEyes;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared.Body.Components;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Systems;
using Content.Shared.Emp;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.CyberEyes;

public sealed class CyberneticEyesEmpSystem : EntitySystem
{
    private static readonly TimeSpan DisruptionDuration =
        TimeSpan.FromSeconds(10);

    [Dependency] private SharedBodySystem _body = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BodyComponent, EmpPulseEvent>(OnEmpPulse);
    }

    private void OnEmpPulse(Entity<BodyComponent> ent, ref EmpPulseEvent args)
    {
        foreach (var (eyeUid, eyeOrgan) in
            _body.GetBodyOrgans(ent.Owner, ent.Comp))
        {
            if (!TryComp<CyberneticEyesEmpComponent>(
                    eyeUid, out var sensitive))
                continue;

            var until = _timing.CurTime + DisruptionDuration;

            if (sensitive.DisabledUntil is not { } old || old < until)
                sensitive.DisabledUntil = until;

            if (eyeOrgan.Enabled)
            {
                var disabled = new OrganEnableChangedEvent(false);
                RaiseLocalEvent(eyeUid, ref disabled);
            }

            args.Affected = true;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query =
            EntityQueryEnumerator<CyberneticEyesEmpComponent, OrganComponent>();

        while (query.MoveNext(out var eyeUid, out var sensitive, out var organ))
        {
            if (sensitive.DisabledUntil is not { } until || now < until)
                continue;

            sensitive.DisabledUntil = null;

            if (organ.Body is not { Valid: true })
                continue;

            var enabled = new OrganEnableChangedEvent(true);
            RaiseLocalEvent(eyeUid, ref enabled);
        }
    }
}
