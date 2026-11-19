using System.Linq;
using Content.Server.Mind;
using Content.Shared.Actions.Events;
using Content.Shared.Clothing;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Stunnable;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunHunterMittsComponent : Component;

[RegisterComponent]
public sealed partial class BitrunHunterPreyComponent : Component;

[RegisterComponent]
public sealed partial class BitrunHunterBoxerComponent : Component
{
    public EntityUid? Target;
    public bool Guard;
    public char PreviousHook;
    public TimeSpan ComboExpires;
    public TimeSpan StaggerExpires;
}

// Training belongs to the mind, so leaving a virtual body does not erase it.
[RegisterComponent]
public sealed partial class BitrunBoxingTrainingComponent : Component
{
    [DataField] public float Experience;
}

[RegisterComponent]
public sealed partial class BitrunRebukedComponent : Component
{
    public TimeSpan Until;
}

/// <summary>Hunter boxing from martial/boxing.dm. Hooks are melee/disarm; guard is an explicit stance.</summary>
public sealed class BitrunHunterBoxingSystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private MindSystem _minds = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private StaminaSystem _stamina = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _timing = default!;

    private static readonly int[] Thresholds = { 0, 100, 250, 500, 900, 1500, 2500 };
    private static readonly int[] SkillChance = { 0, 5, 10, 15, 20, 30, 50 };

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunHunterMittsComponent, ClothingGotEquippedEvent>((Entity<BitrunHunterMittsComponent> ent, ref ClothingGotEquippedEvent args) =>
        {
            if (HasComp<HumanoidAppearanceComponent>(args.Wearer)) EnsureComp<BitrunHunterBoxerComponent>(args.Wearer);
        });
        SubscribeLocalEvent<BitrunHunterMittsComponent, ClothingGotUnequippedEvent>((Entity<BitrunHunterMittsComponent> ent, ref ClothingGotUnequippedEvent args) => RemComp<BitrunHunterBoxerComponent>(args.Wearer));
        SubscribeLocalEvent<BitrunHunterMittsComponent, MeleeHitEvent>(OnHook);
        SubscribeLocalEvent<BitrunHunterMittsComponent, GetVerbsEvent<AlternativeVerb>>(OnGuardVerb);
        SubscribeLocalEvent<MobStateComponent, DisarmAttemptEvent>(OnRightHook);
        SubscribeLocalEvent<BitrunHunterBoxerComponent, AttackedEvent>(OnUnarmedAttack);
        SubscribeLocalEvent<BitrunRebukedComponent, GetMeleeAttackRateEvent>((Entity<BitrunRebukedComponent> ent, ref GetMeleeAttackRateEvent args) =>
        {
            if (ent.Comp.Until > _timing.CurTime) args.Multipliers *= 0.5f;
        });
    }

    private bool Worn(EntityUid user) => HasComp<BitrunHunterBoxerComponent>(user) &&
        _inventory.TryGetSlotEntity(user, "gloves", out var gloves) && HasComp<BitrunHunterMittsComponent>(gloves);

    private bool Prey(EntityUid target) => HasComp<BitrunMegafaunaComponent>(target) || HasComp<BitrunHunterPreyComponent>(target);
    private bool Honorable(EntityUid target) => Worn(target) || Prey(target);

    private BitrunBoxingTrainingComponent? Training(EntityUid user) =>
        _minds.TryGetMind(user, out var mind, out _) ? EnsureComp<BitrunBoxingTrainingComponent>(mind) : null;

    public static int Level(float experience) => Thresholds.Count(t => experience >= t);
    private int Skill(EntityUid user) => Training(user) is {} training ? Level(training.Experience) : 0;
    private int Chance(EntityUid user) => Skill(user) is > 0 and var level ? SkillChance[level - 1] : 0;

    private void OnHook(Entity<BitrunHunterMittsComponent> ent, ref MeleeHitEvent args)
    {
        if (!args.IsHit || args.Handled || !Worn(args.User)) return;
        args.Handled = true;
        if (args.HitEntities.Count > 0) Punch(args.User, args.HitEntities[0], 'H');
    }

    private void OnRightHook(Entity<MobStateComponent> target, ref DisarmAttemptEvent args)
    {
        if (args.Cancelled || !Worn(args.DisarmerUid)) return;
        args.Cancel();
        Punch(args.DisarmerUid, target, 'D');
    }

    private void OnGuardVerb(Entity<BitrunHunterMittsComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !Worn(args.User)) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-boxing-guard"),
            Act = () => { if (Worn(user)) Comp<BitrunHunterBoxerComponent>(user).Guard ^= true; } });
    }

    private void Deal(EntityUid target, float amount, EntityUid source, bool penetration = false) =>
        _damage.TryChangeDamage(target, new DamageSpecifier { DamageDict = { ["Blunt"] = amount } },
            origin: source, armorPenetration: penetration ? 0.1f : 0);

    private void Experience(EntityUid user, EntityUid target, float amount)
    {
        if (Training(user) is not {} training) return;
        var multiplier = 0.5f;
        if (Prey(target))
        {
            var hp = CompOrNull<MobThresholdsComponent>(target)?.Thresholds.Keys.LastOrDefault().Float() ?? 100;
            multiplier = 1 + hp / 500;
        }
        training.Experience += MathF.Round(amount * multiplier);
    }

    private void Extra(EntityUid user, EntityUid target)
    {
        if (Prey(target)) Deal(target, _random.Next(15, 21), user);
    }

    private bool Block(EntityUid boxer, EntityUid attacker, float amount)
    {
        if (!Worn(boxer) || !Comp<BitrunHunterBoxerComponent>(boxer).Guard ||
            !_mobs.IsAlive(boxer) || !Honorable(attacker)) return false;
        Experience(boxer, attacker, amount == 0 ? 2 : amount / 4);
        if (!_random.Prob(Math.Clamp(10 + Chance(boxer), 0, 100) / 100f)) return false;
        if (_transform.InRange(boxer, attacker, 1.5f))
        {
            Deal(attacker, 10, boxer);
            _stamina.TakeStaminaDamage(boxer, 5, source: boxer);
            Extra(boxer, attacker);
        }
        return true;
    }

    private void OnUnarmedAttack(Entity<BitrunHunterBoxerComponent> ent, ref AttackedEvent args)
    {
        if (args.Used != args.User) return;
        var damage = _melee.GetDamage(args.Used, args.User);
        if (Block(ent, args.User, damage.GetTotal().Float())) args.BonusDamage -= damage;
    }

    public bool Punch(EntityUid user, EntityUid target, char hook)
    {
        if (!Worn(user) || !_mobs.IsAlive(user) || !_mobs.IsAlive(target) || !_transform.InRange(user, target, 1.5f)) return false;
        var boxer = Comp<BitrunHunterBoxerComponent>(user);
        var honorable = Honorable(target);
        var multiplier = 1f;
        if (honorable)
        {
            if (boxer.ComboExpires <= _timing.CurTime || boxer.Target != target) boxer.PreviousHook = default;
            if (boxer.PreviousHook != default)
            {
                multiplier = boxer.PreviousHook == hook ? 0.5f : 1.5f;
                if (boxer.PreviousHook != hook) Extra(user, target);
                boxer.PreviousHook = default;
            }
            else boxer.PreviousHook = hook;
            boxer.Target = target;
            boxer.ComboExpires = _timing.CurTime + TimeSpan.FromSeconds(6);
        }
        var amount = _random.Next(5, 11) + (honorable ? MathF.Round(Skill(user) * multiplier) : 0);
        if (Block(target, user, amount)) return false;
        Deal(target, amount, user, true);
        if (!honorable || _mobs.IsDead(target)) return true;
        Experience(user, target, amount / 5);
        if (!_random.Prob(Math.Clamp(10 + Chance(user) - Chance(target), 0, 100) / 100f)) return true;
        ApplyCritical(user, target, amount);
        Experience(user, target, amount / 2);
        return true;
    }

    public void ApplyCritical(EntityUid user, EntityUid target, float amount)
    {
        if (Prey(target))
        {
            EnsureComp<BitrunRebukedComponent>(target).Until = _timing.CurTime + TimeSpan.FromSeconds(30);
            Deal(target, amount * 2, user, true);
            Heal(user, MathF.Round(amount / 3));
        }
        else
        {
            var defender = EnsureComp<BitrunHunterBoxerComponent>(target);
            if (defender.StaggerExpires > _timing.CurTime)
            {
                _stun.TryKnockdown(target, TimeSpan.FromSeconds(20), true);
                _stun.TryParalyze(target, TimeSpan.FromSeconds(10), true);
            }
            else
            {
                defender.StaggerExpires = _timing.CurTime + TimeSpan.FromSeconds(3);
                _stun.TrySlowdown(target, TimeSpan.FromSeconds(3), true, 0.7f, 0.7f);
            }
        }
    }

    private void Heal(EntityUid user, float amount)
    {
        if (!TryComp<DamageableComponent>(user, out var damage)) return;
        var healing = new DamageSpecifier();
        foreach (var group in new[] { new[] { "Blunt", "Slash", "Piercing" }, new[] { "Heat", "Cold", "Shock" }, new[] { "Poison" } })
        {
            var total = group.Sum(t => damage.Damage.DamageDict.GetValueOrDefault(t).Float());
            if (total <= 0) continue;
            foreach (var type in group)
                healing.DamageDict[type] = -MathF.Min(total, amount) * damage.Damage.DamageDict.GetValueOrDefault(type).Float() / total;
        }
        _damage.TryChangeDamage(user, healing, true);
    }
}
