using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.Flash;
using Content.Shared.Traits.Assorted.Components;
using Robust.Shared.Random;

namespace Content.Server.Traits;

public sealed class PhotophobiaSystem : EntitySystem
{
    [Dependency] private BlindableSystem _blindable = default!;
    [Dependency] private IRobustRandom _random = default!;

    public override void Initialize() =>
        SubscribeLocalEvent<EyeDamageOnFlashingComponent, AfterFlashedEvent>(OnFlashed);

    private void OnFlashed(EntityUid uid, EyeDamageOnFlashingComponent component, ref AfterFlashedEvent args)
    {
        if (args.Target == uid && _random.Prob(component.EyeDamageChance))
            _blindable.AdjustEyeDamage(uid, component.EyeDamage);
    }
}
