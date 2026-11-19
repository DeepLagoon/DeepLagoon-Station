using System.Numerics;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Jittering;
using Content.Shared.Speech.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Popups;
using Content.Shared.Speech.Muting;
using Content.Shared.StatusEffect;
using Content.Shared.StepTrigger.Systems;
using Content.Shared.Stunnable;
using Content.Shared.Verbs;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunRuneCarverComponent : Component
{
    [DataField] public int Selected;
    public int PendingKind;
    public readonly List<EntityUid> Runes = new();
    public EntityUid? User;
    public EntityCoordinates Position;
    public MapCoordinates UserPosition;
    public float InitialDamage;
    public TimeSpan Finish;
}

[RegisterComponent]
public sealed partial class BitrunCarvedRuneComponent : Component
{
    [DataField] public int Kind;
    [DataField] public int Charges = -1;
    [DataField] public EntityUid? Carver;
    public TimeSpan NextTrigger;
}

/// <summary>Source five-second carving, three-rune limit, and reusable/charged rune effects.</summary>
public sealed class BitrunRuneCarverSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private StaminaSystem _stamina = default!;
    [Dependency] private StatusEffectsSystem _status = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private SharedJitteringSystem _jitter = default!;
    [Dependency] private SharedStutteringSystem _stutter = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunRuneCarverComponent, UseInHandEvent>(OnSelect);
        SubscribeLocalEvent<BitrunRuneCarverComponent, AfterInteractEvent>(OnCarve);
        SubscribeLocalEvent<BitrunRuneCarverComponent, GetVerbsEvent<AlternativeVerb>>(OnBreakVerb);
        SubscribeLocalEvent<BitrunCarvedRuneComponent, StepTriggeredOnEvent>(OnStep);
        SubscribeLocalEvent<BitrunCarvedRuneComponent, InteractUsingEvent>(OnDispel);
    }

    private bool IsHeretic(EntityUid user) => HasComp<BitrunHereticActorComponent>(user) || _factions.IsMember(user, "BitrunHeretic");

    private void OnSelect(Entity<BitrunRuneCarverComponent> knife, ref UseInHandEvent args)
    {
        if (args.Handled || !IsHeretic(args.User)) return;
        args.Handled = true;
        knife.Comp.Selected = (knife.Comp.Selected + 1) % 3;
        _popup.PopupEntity(Loc.GetString("bitrun-rune-selected-" + knife.Comp.Selected), knife, args.User);
    }

    private void OnCarve(Entity<BitrunRuneCarverComponent> knife, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || !IsHeretic(args.User) || args.Target != null || knife.Comp.User != null ||
            !_turf.TryGetTileRef(args.ClickLocation, out var tile) || tile.Value.Tile.IsEmpty ||
            _tiles[tile.Value.Tile.TypeId].ID.Contains("Lava", StringComparison.OrdinalIgnoreCase)) return;
        knife.Comp.Runes.RemoveAll(rune => Deleted(rune));
        if (knife.Comp.Runes.Count >= 3) return;
        var position = _transform.ToMapCoordinates(args.ClickLocation);
        var runes = EntityQueryEnumerator<BitrunCarvedRuneComponent, TransformComponent>();
        while (runes.MoveNext(out var uid, out _, out _))
        {
            var other = _transform.GetMapCoordinates(uid);
            if (other.MapId == position.MapId && Vector2.DistanceSquared(other.Position, position.Position) <= 2) return;
        }
        args.Handled = true;
        knife.Comp.User = args.User;
        knife.Comp.PendingKind = knife.Comp.Selected;
        knife.Comp.Position = args.ClickLocation;
        knife.Comp.UserPosition = _transform.GetMapCoordinates(args.User);
        knife.Comp.InitialDamage = (float)(CompOrNull<DamageableComponent>(args.User)?.TotalDamage ?? 0);
        knife.Comp.Finish = _timing.CurTime + TimeSpan.FromSeconds(5);
        _popup.PopupEntity(Loc.GetString("bitrun-rune-carving"), knife, args.User);
    }

    private void OnBreakVerb(Entity<BitrunRuneCarverComponent> knife, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !IsHeretic(args.User)) return;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-rune-break"), Act = () =>
        {
            foreach (var rune in knife.Comp.Runes) if (!Deleted(rune)) QueueDel(rune);
            knife.Comp.Runes.Clear();
        }});
    }

    private void OnDispel(Entity<BitrunCarvedRuneComponent> rune, ref InteractUsingEvent args)
    {
        if (!args.Handled && HasComp<BitrunRuneCarverComponent>(args.Used))
        {
            args.Handled = true;
            QueueDel(rune);
        }
    }

    private void OnStep(Entity<BitrunCarvedRuneComponent> rune, ref StepTriggeredOnEvent args)
    {
        if (IsHeretic(args.Tripper) || rune.Comp.NextTrigger > _timing.CurTime || rune.Comp.Charges == 0) return;
        if (rune.Comp.Kind != 0 && !HasComp<HumanoidAppearanceComponent>(args.Tripper)) return;
        rune.Comp.NextTrigger = _timing.CurTime + TimeSpan.FromSeconds(rune.Comp.Kind switch { 0 => 5, 1 => 45, _ => 20 });
        switch (rune.Comp.Kind)
        {
            case 0:
                if (rune.Comp.Carver is { } owner && !Deleted(owner))
                    _popup.PopupEntity(Loc.GetString("bitrun-rune-alert", ("target", Name(args.Tripper)),
                        ("position", _transform.GetMapCoordinates(rune).Position.ToString())), owner, owner);
                break;
            case 1:
                _stun.TryParalyze(args.Tripper, TimeSpan.FromSeconds(5), true);
                var damage = new DamageSpecifier();
                damage.DamageDict.Add("Blunt", 40);
                _damage.TryChangeDamage(args.Tripper, damage, origin: rune.Comp.Carver);
                break;
            default:
                _stamina.TakeStaminaDamage(args.Tripper, 80, source: rune.Comp.Carver, with: rune);
                _status.TryAddStatusEffect<TemporaryBlindnessComponent>(args.Tripper, "TemporaryBlindness", TimeSpan.FromSeconds(4), true);
                _status.TryAddStatusEffect<MutedComponent>(args.Tripper, "Muted", TimeSpan.FromSeconds(20), true);
                _jitter.DoJitter(args.Tripper, TimeSpan.FromSeconds(20), true);
                _stutter.DoStutter(args.Tripper, TimeSpan.FromMinutes(1), true);
                break;
        }
        if (rune.Comp.Charges > 0 && --rune.Comp.Charges == 0) QueueDel(rune);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunRuneCarverComponent>();
        while (query.MoveNext(out var uid, out var knife))
        {
            if (knife.User is not { } user) continue;
            if (Deleted(user)) { knife.User = null; continue; }
            var now = _transform.GetMapCoordinates(user);
            if (now.MapId != knife.UserPosition.MapId || Vector2.DistanceSquared(now.Position, knife.UserPosition.Position) > 0.01 ||
                (float)(CompOrNull<DamageableComponent>(user)?.TotalDamage ?? 0) > knife.InitialDamage ||
                !_hands.IsHolding(user, uid, out _))
            { knife.User = null; continue; }
            if (knife.Finish > _timing.CurTime) continue;
            var rune = Spawn(knife.PendingKind switch { 0 => "BitrunAlertCarving", 1 => "BitrunGraspingCarving", _ => "BitrunMadCarving" }, knife.Position);
            Comp<BitrunCarvedRuneComponent>(rune).Carver = user;
            knife.Runes.Add(rune);
            knife.User = null;
        }
    }
}
