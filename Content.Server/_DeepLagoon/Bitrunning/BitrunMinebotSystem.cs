using System.Linq;
using System.Numerics;
using Content.Server.Gatherable;
using Content.Server.Gatherable.Components;
using Content.Server.NPC.Systems;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mining.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

public enum BitrunMinebotMode : byte { Collect, Combat, Mine, Idle }

[RegisterComponent]
public sealed partial class BitrunMinebotComponent : Component
{
    [DataField(required: true)] public EntProtoId GunPrototype;
    public EntityUid Gun;
    public EntityUid? Owner;
    public EntityUid? Target;
    public BitrunMinebotMode Mode;
    public bool Follow = true;
    public bool AutoDefend = true;
    public int MinimumDistance = 3;
    public TimeSpan NextUpdate;
}

[RegisterComponent]
public sealed partial class BitrunMinebotOwnerComponent : Component;

/// <summary>Minebot collection/combat/mining and owner commands from basic/minebots.</summary>
public sealed class BitrunMinebotSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private SharedGunSystem _guns = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPointLightSystem _light = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private GatherableSystem _gather = default!;
    [Dependency] private BitrunKineticSystem _kinetic = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    private Container Ore(EntityUid bot) => _containers.EnsureContainer<Container>(bot, "bitrun-minebot-ore");

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunMinebotComponent, MapInitEvent>((Entity<BitrunMinebotComponent> ent, ref MapInitEvent args) =>
        {
            RemComp<BarotraumaComponent>(ent);
            RemComp<RespiratorComponent>(ent);
            RemComp<TemperatureComponent>(ent);
            RemComp<StaminaComponent>(ent);
            Ore(ent);
            var slot = _containers.EnsureContainer<ContainerSlot>(ent, "bitrun-minebot-gun");
            ent.Comp.Gun = Spawn(ent.Comp.GunPrototype, Transform(ent).Coordinates);
            _containers.Insert(ent.Comp.Gun, slot);
        });
        SubscribeLocalEvent<BitrunMinebotComponent, InteractHandEvent>((Entity<BitrunMinebotComponent> ent, ref InteractHandEvent args) =>
        {
            if (args.Handled) return;
            args.Handled = true;
            Claim(ent, args.User);
            SetMode(ent, ent.Comp.Mode == BitrunMinebotMode.Idle ? BitrunMinebotMode.Collect : ent.Comp.Mode);
        });
        SubscribeLocalEvent<BitrunMinebotComponent, InteractUsingEvent>(OnUse);
        SubscribeLocalEvent<BitrunMinebotComponent, BitrunMinebotRepairEvent>(OnRepair);
        SubscribeLocalEvent<BitrunMinebotComponent, GetVerbsEvent<AlternativeVerb>>(OnCommands);
        SubscribeLocalEvent<BitrunMinebotComponent, AttackedEvent>((Entity<BitrunMinebotComponent> ent, ref AttackedEvent args) => Defend(ent, args.User));
        SubscribeLocalEvent<BitrunMinebotOwnerComponent, AttackedEvent>((Entity<BitrunMinebotOwnerComponent> ent, ref AttackedEvent args) =>
        {
            var query = EntityQueryEnumerator<BitrunMinebotComponent>();
            while (query.MoveNext(out var uid, out var bot))
                if (bot.Owner == ent.Owner && _interaction.InRangeUnobstructed(uid, ent.Owner, range: 7)) Defend((uid, bot), args.User);
        });
        SubscribeLocalEvent<BitrunMinebotComponent, MobStateChangedEvent>((Entity<BitrunMinebotComponent> ent, ref MobStateChangedEvent args) =>
        {
            if (args.NewMobState != MobState.Dead) return;
            Dump(ent);
            if (TryComp<BitrunKineticAcceleratorComponent>(ent.Comp.Gun, out var gun))
                foreach (var kit in _containers.EnsureContainer<Container>(ent.Comp.Gun, "bitrun-kinetic-modkits").ContainedEntities.ToArray())
                    _kinetic.Remove((ent.Comp.Gun, gun), kit, ent);
            QueueDel(ent.Comp.Gun);
            QueueDel(ent);
        });
    }

    private void Claim(Entity<BitrunMinebotComponent> bot, EntityUid user)
    {
        if (bot.Comp.Owner != null) return;
        bot.Comp.Owner = user;
        EnsureComp<BitrunMinebotOwnerComponent>(user);
    }

    public void SetMode(Entity<BitrunMinebotComponent> bot, BitrunMinebotMode mode)
    {
        bot.Comp.Mode = mode;
        bot.Comp.Target = null;
        _steering.Unregister(bot);
        _appearance.SetData(bot, Content.Shared.Toggleable.ToggleableVisuals.Enabled, mode == BitrunMinebotMode.Combat);
    }

    private void Defend(Entity<BitrunMinebotComponent> bot, EntityUid attacker)
    {
        if (!bot.Comp.AutoDefend || attacker == bot.Comp.Owner || _mobs.IsDead(bot)) return;
        SetMode(bot, BitrunMinebotMode.Combat);
        bot.Comp.Target = attacker;
    }

    private void OnCommands(Entity<BitrunMinebotComponent> bot, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || _mobs.IsDead(bot)) return;
        var user = args.User;
        foreach (var mode in Enum.GetValues<BitrunMinebotMode>())
        {
            var selected = mode;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-minebot-mode-" + mode.ToString().ToLowerInvariant()),
                Act = () => { Claim(bot, user); SetMode(bot, selected); } });
        }
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-minebot-dump"), Act = () => Dump(bot) });
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-minebot-light"), Act = () => _light.SetEnabled(bot, !Comp<Robust.Server.GameObjects.PointLightComponent>(bot).Enabled) });
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-minebot-follow"), Act = () => { Claim(bot, user); bot.Comp.Follow = !bot.Comp.Follow; } });
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-minebot-defend"), Act = () => bot.Comp.AutoDefend = !bot.Comp.AutoDefend });
        for (var distance = 0; distance <= 5; distance++)
        {
            var selected = distance;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-minebot-distance", ("distance", distance)), Act = () => bot.Comp.MinimumDistance = selected });
        }
    }

    public void Dump(EntityUid bot)
    {
        foreach (var item in Ore(bot).ContainedEntities.ToArray())
        {
            _containers.Remove(item, Ore(bot));
            _transform.SetCoordinates(item, Transform(bot).Coordinates);
        }
    }

    private void OnUse(Entity<BitrunMinebotComponent> bot, ref InteractUsingEvent args)
    {
        if (args.Handled) return;
        if (HasComp<BitrunKineticModkitComponent>(args.Used) || _tools.HasQuality(args.Used, "Prying"))
        {
            var use = new InteractUsingEvent(args.User, args.Used, bot.Comp.Gun, args.ClickLocation);
            RaiseLocalEvent(bot.Comp.Gun, use);
            args.Handled = use.Handled;
        }
        else if (bot.Comp.Mode != BitrunMinebotMode.Combat && _tools.HasQuality(args.Used, "Welding"))
            args.Handled = _tools.UseTool(args.Used, args.User, bot, 0, "Welding", new BitrunMinebotRepairEvent(), fuel: 1);
    }

    private void OnRepair(Entity<BitrunMinebotComponent> bot, ref BitrunMinebotRepairEvent args)
    {
        if (args.Cancelled || args.Handled || bot.Comp.Mode == BitrunMinebotMode.Combat || !TryComp<DamageableComponent>(bot, out var damageable)) return;
        var heal = new DamageSpecifier();
        var remaining = 15f;
        foreach (var kind in new[] {"Blunt", "Slash", "Piercing"})
        {
            var amount = Math.Min(remaining, (float)damageable.Damage.DamageDict.GetValueOrDefault(kind));
            heal.DamageDict[kind] = -amount;
            remaining -= amount;
        }
        _damage.TryChangeDamage(bot, heal, true, origin: args.User);
        args.Handled = true;
    }

    private bool IsOre(EntityUid uid) => HasComp<StackComponent>(uid) &&
        (_tags.HasTag(uid, "Ore") || MetaData(uid).EntityPrototype?.ID.EndsWith("Ore", StringComparison.Ordinal) == true);

    private void MoveTo(EntityUid bot, EntityUid target, float stop)
    {
        if ((_transform.GetWorldPosition(bot) - _transform.GetWorldPosition(target)).LengthSquared() <= stop * stop) _steering.Unregister(bot);
        else _steering.TryRegister(bot, Transform(target).Coordinates);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunMinebotComponent>();
        while (query.MoveNext(out var uid, out var bot))
        {
            if (bot.NextUpdate > _timing.CurTime || _mobs.IsDead(uid)) continue;
            bot.NextUpdate = _timing.CurTime + TimeSpan.FromSeconds(0.25);
            if (bot.Mode == BitrunMinebotMode.Idle) { _steering.Unregister(uid); continue; }
            if (bot.Mode == BitrunMinebotMode.Combat)
            {
                if (bot.Target is not { } current || Deleted(current) || _mobs.IsDead(current) || Transform(current).MapUid != Transform(uid).MapUid)
                    bot.Target = _factions.GetNearbyHostiles(uid, 7).Where(target => !_mobs.IsDead(target)).Select(target => (EntityUid?)target).FirstOrDefault();
                if (bot.Target is not { } target) continue;
                var delta = _transform.GetWorldPosition(uid) - _transform.GetWorldPosition(target);
                if (delta.LengthSquared() < bot.MinimumDistance * bot.MinimumDistance && delta.LengthSquared() > 0.01f)
                    _steering.TryRegister(uid, Transform(uid).Coordinates.Offset(Vector2.Normalize(delta) * 2));
                else if (delta.LengthSquared() > 9) MoveTo(uid, target, 3);
                if (_interaction.InRangeUnobstructed(uid, target, range: 7) && TryComp<GunComponent>(bot.Gun, out var gun))
                    _guns.AttemptShoot(uid, bot.Gun, gun, Transform(target).Coordinates);
                continue;
            }
            var ore = _lookup.GetEntitiesInRange<ItemComponent>(Transform(uid).Coordinates, 7)
                .Where(item => IsOre(item) && !_containers.IsEntityInContainer(item)).OrderBy(item => (_transform.GetWorldPosition(uid) - _transform.GetWorldPosition(item)).LengthSquared()).FirstOrDefault();
            if (ore.Owner.Valid)
            {
                MoveTo(uid, ore, 1);
                if (_interaction.InRangeUnobstructed(uid, ore.Owner, range: 1.5f)) _containers.Insert(ore.Owner, Ore(uid));
                continue;
            }
            if (bot.Mode == BitrunMinebotMode.Mine)
            {
                var rock = _lookup.GetEntitiesInRange<MiningScannerViewableComponent>(Transform(uid).Coordinates, 7)
                    .Where(rock => !MetaData(rock).EntityName.Contains("gibtonite", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(rock => (_transform.GetWorldPosition(uid) - _transform.GetWorldPosition(rock)).LengthSquared()).FirstOrDefault();
                if (rock.Owner.Valid)
                {
                    MoveTo(uid, rock, 1);
                    if (_interaction.InRangeUnobstructed(uid, rock.Owner, range: 1.5f) && TryComp<GatherableComponent>(rock, out var gatherable) && !gatherable.Gathered)
                        _gather.Gather(rock, uid, gatherable);
                    continue;
                }
            }
            if (bot.Follow && bot.Owner is { } owner && !Deleted(owner) && Transform(owner).MapUid == Transform(uid).MapUid) MoveTo(uid, owner, 2);
        }
    }
}
