using Content.Shared._Shitmed.Targeting;
using Content.Shared.Flash;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunLaserPointerComponent : Component
{
    [DataField] public int Energy = 10;
    [DataField] public int Rating = 1;
    public bool RechargeLocked;
    public TimeSpan NextRecharge;
}

public sealed class BitrunLaserPointerSystem : EntitySystem
{
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedFlashSystem _flash = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize() => SubscribeLocalEvent<BitrunLaserPointerComponent, AfterInteractEvent>(OnPoint);

    private void OnPoint(Entity<BitrunLaserPointerComponent> pointer, ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } target ||
            !_interaction.InRangeUnobstructed(args.User, target, range: 7))
            return;
        args.Handled = true;
        if (pointer.Comp.RechargeLocked || pointer.Comp.Energy <= 0)
        {
            _popup.PopupEntity(Loc.GetString("bitrun-pointer-recharging"), pointer, args.User);
            return;
        }
        pointer.Comp.Energy--;
        pointer.Comp.RechargeLocked = pointer.Comp.Energy == 0;
        Spawn("BitrunBlueLaserDot", Transform(target).Coordinates);
        // The station targets the head as a whole rather than a separate eye zone.
        if (HasComp<HumanoidAppearanceComponent>(target) &&
            TryComp<TargetingComponent>(args.User, out var targeting) && targeting.Target == TargetBodyPart.Head &&
            _random.Prob(Math.Min(1, 0.3f * pointer.Comp.Rating)))
            _flash.Flash(target, args.User, pointer, TimeSpan.FromSeconds(2), 1);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunLaserPointerComponent>();
        while (query.MoveNext(out _, out var pointer))
        {
            if (pointer.Energy >= 10 || pointer.NextRecharge > _timing.CurTime)
                continue;
            pointer.NextRecharge = _timing.CurTime + TimeSpan.FromSeconds(1);
            if (!_random.Prob((10 + pointer.Rating * 10) / 100f))
                continue;
            pointer.Energy++;
            if (pointer.Energy >= 10) pointer.RechargeLocked = false;
        }
    }
}
