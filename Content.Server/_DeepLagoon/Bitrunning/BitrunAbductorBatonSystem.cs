using System.Numerics;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Robust.Shared.Prototypes;
using Content.Shared.Bed.Sleep;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Jittering;
using Content.Shared.Popups;
using Content.Shared.Speech.EntitySystems;
using Content.Shared.StatusEffect;
using Content.Shared.Stunnable;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunAbductorActorComponent : Component;

[RegisterComponent]
public sealed partial class BitrunAbductorBatonComponent : Component
{
    [DataField] public int Mode;
    [DataField] public Dictionary<int, ChameleonClothingAppearance> ModeVisuals = new();
    public float InitialDamage;
    public EntityUid? User;
    public EntityUid? Target;
    public MapCoordinates UserPosition;
    public MapCoordinates TargetPosition;
    public TimeSpan Finish;
}

public sealed class BitrunAbductorBatonSystem : EntitySystem
{
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private SharedJitteringSystem _jitter = default!;
    [Dependency] private SharedStutteringSystem _stutter = default!;
    [Dependency] private StatusEffectsSystem _status = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedCuffableSystem _cuffs = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedItemSystem _items = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunAbductorBatonComponent, UseInHandEvent>(OnSelect);
        SubscribeLocalEvent<BitrunAbductorBatonComponent, AfterInteractEvent>(OnInteract);
        SubscribeLocalEvent<BitrunAbductorBatonComponent, MapInitEvent>((Entity<BitrunAbductorBatonComponent> ent, ref MapInitEvent args) => UpdateVisuals(ent));
    }

    private void UpdateVisuals(Entity<BitrunAbductorBatonComponent> baton)
    {
        _appearance.SetData(baton, BitrunEquipmentVisuals.Mode, baton.Comp.Mode);
        if (baton.Comp.ModeVisuals.TryGetValue(baton.Comp.Mode, out var visuals))
            _items.CopyVisuals(baton, visuals);
    }

    private void OnSelect(Entity<BitrunAbductorBatonComponent> baton, ref UseInHandEvent args)
    {
        if (args.Handled || !HasComp<BitrunAbductorActorComponent>(args.User)) return;
        args.Handled = true;
        baton.Comp.Mode = (baton.Comp.Mode + 1) % 4;
        UpdateVisuals(baton);
        _popup.PopupEntity(Loc.GetString("bitrun-abductor-mode-" + baton.Comp.Mode), baton, args.User);
    }

    private void OnInteract(Entity<BitrunAbductorBatonComponent> baton, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target ||
            !HasComp<MobStateComponent>(target)) return;
        args.Handled = true;
        if (!HasComp<BitrunAbductorActorComponent>(args.User))
        {
            _popup.PopupEntity(Loc.GetString("bitrun-abductor-rejected"), baton, args.User);
            return;
        }
        switch (baton.Comp.Mode)
        {
            case 0:
                _stun.TryParalyze(target, TimeSpan.FromSeconds(14), true);
                _jitter.DoJitter(target, TimeSpan.FromSeconds(40), true);
                _stutter.DoStutter(target, TimeSpan.FromSeconds(16), true);
                break;
            case 1:
                if (!HasComp<HumanoidAppearanceComponent>(target)) return;
                if (HasComp<StunnedComponent>(target) || HasComp<KnockedDownComponent>(target) || HasComp<SleepingComponent>(target))
                    _status.TryAddStatusEffect<ForcedSleepingComponent>(target, "ForcedSleep", TimeSpan.FromMinutes(2), true);
                else
                    _popup.PopupEntity(Loc.GetString("bitrun-abductor-stun-first"), baton, args.User);
                break;
            case 2:
                if (baton.Comp.User != null || !TryComp<CuffableComponent>(target, out var cuffable) || _cuffs.IsCuffed((target, cuffable))) return;
                baton.Comp.User = args.User;
                baton.Comp.Target = target;
                baton.Comp.UserPosition = _transform.GetMapCoordinates(args.User);
                baton.Comp.TargetPosition = _transform.GetMapCoordinates(target);
                baton.Comp.InitialDamage = (float)(CompOrNull<DamageableComponent>(args.User)?.TotalDamage ?? 0);
                baton.Comp.Finish = _timing.CurTime + TimeSpan.FromSeconds(3);
                break;
            case 3:
                var species = TryComp<HumanoidAppearanceComponent>(target, out var humanoid)
                    ? Loc.GetString(_prototypes.Index(humanoid.Species).Name) : Loc.GetString("bitrun-abductor-unknown-species");
                _popup.PopupEntity(Loc.GetString("bitrun-abductor-probe", ("species", species)), baton, args.User);
                break;
        }
    }

    private bool StillAt(EntityUid uid, MapCoordinates position)
    {
        var current = _transform.GetMapCoordinates(uid);
        return current.MapId == position.MapId && Vector2.DistanceSquared(current.Position, position.Position) < 0.01;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunAbductorBatonComponent>();
        while (query.MoveNext(out var uid, out var baton))
        {
            if (baton.User is not { } user || baton.Target is not { } target) continue;
            if (Deleted(user) || Deleted(target) || !StillAt(user, baton.UserPosition) || !StillAt(target, baton.TargetPosition) ||
                (float)(CompOrNull<DamageableComponent>(user)?.TotalDamage ?? 0) > baton.InitialDamage ||
                !_hands.IsHolding(user, uid, out _) || !_interaction.InRangeUnobstructed(user, target))
            { baton.User = null; baton.Target = null; continue; }
            if (baton.Finish > _timing.CurTime) continue;
            var cuffs = Spawn("BitrunAbductorEnergyCuffs", Transform(target).Coordinates);
            if (!_cuffs.TryAddNewCuffs(target, user, cuffs)) QueueDel(cuffs);
            baton.User = null;
            baton.Target = null;
        }
    }
}
