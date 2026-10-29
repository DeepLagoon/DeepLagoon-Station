using System.Linq;
using Content.Server.Popups;
using Content.Server.PowerCell;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.ActionBlocker;
using Content.Shared.Actions;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Fluids;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Stunnable;
using Content.Shared.Verbs;
using Content.Shared.Inventory;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Physics.Components;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class LustWishEquipmentSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private InteractionPanelSystem _panels = default!;
    [Dependency] private InteractionPanelArousalSystem _arousal = default!;
    [Dependency] private LustWishWornEffectsSystem _effects = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private SharedBatterySystem _battery = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private ISharedAdminLogManager _logs = default!;
    private TimeSpan _nextUpdate;
    private readonly Dictionary<EntityUid, EntityUid> _watchers = new();
    private readonly Dictionary<EntityUid, LustWishGearStateEvent> _lastStates = new();
    private readonly Dictionary<Guid, Pending> _pending = new();
    private readonly Dictionary<(EntityUid, EntityUid), TimeSpan> _askCooldown = new();
    private readonly HashSet<(EntityUid, EntityUid)> _blocked = new();
    private sealed record Pending(EntityUid User, EntityUid Target, EntityUid Item, EntityUid? Held,
        string Operation, string Slot, string Value, ICommonSession Recipient, TimeSpan Expires, bool Remote, bool Native);

    public override void Initialize()
    {
        SubscribeNetworkEvent<LustWishGearRequestEvent>(OnRequest);
        SubscribeLocalEvent<BlueMoonProtectionComponent, BlueMoonProtectionAttachEvent>((uid, _, args) =>
        {
            if (args.Cancelled || args.Handled || args.Target is not { } target) return;
            args.Handled = true;
            Apply(args.User, target, uid, uid, "attach", "penis", "", false, protectionCompleted: true);
        });
        InitializeNativeInventory();
        SubscribeNetworkEvent<LustWishSetColorEvent>(OnSetColor);
        SubscribeLocalEvent<LustWishPaletteComponent, LustWishChooseColorEvent>(OnChooseColor);
        SubscribeNetworkEvent<InteractionPanelAnswerEvent>(OnAnswer);
        SubscribeLocalEvent<LustWishAccessoryComponent, ComponentShutdown>(OnAccessoryShutdown);
        SubscribeLocalEvent<LustWishAccessoryComponent, ClothingGotEquippedEvent>((Entity<LustWishAccessoryComponent> ent, ref ClothingGotEquippedEvent ev) =>
        {
            if (ent.Comp.InventorySlots.Length == 0 || ev.Clothing.InSlot is not { } slot || !ent.Comp.InventorySlots.Contains(slot)) return;
            ent.Comp.Slot = slot; ent.Comp.Wearer = ev.Wearer;
            Refresh(ev.Wearer); SendWatchers(ev.Wearer);
        });
        SubscribeLocalEvent<LustWishAccessoryComponent, ClothingGotUnequippedEvent>((Entity<LustWishAccessoryComponent> ent, ref ClothingGotUnequippedEvent ev) =>
        {
            if (ent.Comp.InventorySlots.Length == 0 || ent.Comp.Wearer != ev.Wearer) return;
            StopLeash(ent.Comp); ent.Comp.Wearer = null; ent.Comp.Active = false;
            Refresh(ev.Wearer); SendWatchers(ev.Wearer);
        });
        SubscribeLocalEvent<LustWishWornEffectsComponent, LustWishEscapeEvent>(OnEscape);
        SubscribeLocalEvent<LustWishWornEffectsComponent, GetVerbsEvent<AlternativeVerb>>((Entity<LustWishWornEffectsComponent> ent, ref GetVerbsEvent<AlternativeVerb> ev) =>
        {
            if (ev.User != ent.Owner || !Alive(ent.Owner) || !_blocker.CanConsciouslyPerformAction(ent.Owner)) return;
            ev.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-interaction-panel-tab-gear"), Act = () => _panels.OpenPanel(ent.Owner, ent.Owner) });
        });
        SubscribeLocalEvent<LustWishWornEffectsComponent, EntRemovedFromContainerMessage>((Entity<LustWishWornEffectsComponent> ent, ref EntRemovedFromContainerMessage ev) =>
        {
            if (!TryComp<LustWishAccessoryComponent>(ev.Entity, out var accessory) || accessory.Wearer != ent.Owner) return;
            StopLeash(accessory); accessory.Wearer = null; accessory.Active = false;
            if (accessory.InventorySlots.Length == 0 && TryComp<LustWishPaletteComponent>(ev.Entity, out var palette)) _actions.RemoveAction(ent.Owner, palette.ColorAction);
            if (!Terminating(ent.Owner)) Refresh(ent.Owner);
        });
        SubscribeLocalEvent<LustWishRemoteComponent, UseInHandEvent>(OnRemote);
        SubscribeLocalEvent<LustWishRemoteComponent, GetVerbsEvent<AlternativeVerb>>(OnRemoteVerbs);
        SubscribeLocalEvent<LustWishPaletteComponent, GetVerbsEvent<AlternativeVerb>>(OnPaletteVerbs);
        SubscribeLocalEvent<LustWishKeyComponent, InteractUsingEvent>(OnCopyKey);
        SubscribeLocalEvent<LustWishPackComponent, UseInHandEvent>(OnPack);
        SubscribeLocalEvent<LustWishWipeComponent, SolutionContainerChangedEvent>((Entity<LustWishWipeComponent> ent, ref SolutionContainerChangedEvent ev) =>
        {
            if (ent.Comp.Used || !_solutions.TryGetSolution(ent.Owner, "absorbed", out _, out var solution) || solution.Volume <= 0) return;
            ent.Comp.Used = true;
            RemCompDeferred<AbsorbentComponent>(ent.Owner);
            var metadata = EntityManager.System<MetaDataSystem>();
            metadata.SetEntityName(ent.Owner, Loc.GetString("dl-lustwish-wipe-used"));
            metadata.SetEntityDescription(ent.Owner, Loc.GetString("dl-lustwish-wipe-used-description"));
        });
        SubscribeLocalEvent<LustWishFurnitureKitComponent, UseInHandEvent>(OnFurniture);
        SubscribeLocalEvent<LustWishFurnitureKitComponent, LustWishBuildEvent>(OnBuild);
        SubscribeLocalEvent<LustWishWornEffectsComponent, ContainerIsRemovingAttemptEvent>((Entity<LustWishWornEffectsComponent> ent, ref ContainerIsRemovingAttemptEvent ev) =>
        {
            var containerId = ev.Container.ID;
            if (TryComp<LustWishAccessoryComponent>(ev.EntityUid, out var item) && item.Wearer == ent.Owner &&
                (item.Locked || containerId.StartsWith(LustWishEquipment.ContainerPrefix, StringComparison.Ordinal))) ev.Cancel();
        });
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => { _watchers.Clear(); _lastStates.Clear(); _pending.Clear(); _askCooldown.Clear(); _blocked.Clear(); });
    }

    private bool Alive(EntityUid uid) => Exists(uid) && TryComp<MobStateComponent>(uid, out var mob) && mob.CurrentState == MobState.Alive;
    private InteractionPanelConsent Consent(EntityUid uid) => TryComp<InteractionPanelPreferencesComponent>(uid, out var prefs)
        ? prefs.Profile.GetInteractionPanelConsent(InteractionPanelCategory.Erotic) : InteractionPanelConsent.Ask;
    private bool Accessible(EntityUid user, EntityUid target) => Alive(user) && Alive(target) &&
        HasComp<HumanoidAppearanceComponent>(target) && _interaction.InRangeAndAccessible(user, target);
    public IEnumerable<(EntityUid Uid, LustWishAccessoryComponent Accessory)> Attached(EntityUid wearer)
    {
        if (TryComp<InventoryComponent>(wearer, out var inventory))
            foreach (var slot in inventory.Slots)
                if (_inventory.TryGetSlotEntity(wearer, slot.Name, out var item, inventory) &&
                    TryComp<LustWishAccessoryComponent>(item, out var accessory) && accessory.Wearer == wearer && accessory.InventorySlots.Contains(slot.Name))
                    yield return (item.Value, accessory);
        foreach (var slot in LustWishEquipment.Slots)
            if (_containers.TryGetContainer(wearer, LustWishEquipment.ContainerPrefix + slot, out var container))
                foreach (var uid in container.ContainedEntities)
                    if (TryComp<LustWishAccessoryComponent>(uid, out var accessory) && accessory.Wearer == wearer)
                        yield return (uid, accessory);
    }
    private EntityUid? At(EntityUid target, string slot) => Attached(target).Where(a => a.Accessory.Slot == slot).Select(a => (EntityUid?)a.Uid).FirstOrDefault();

    private void OnRequest(LustWishGearRequestEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user || !TryGetEntity(args.Target, out var maybe) || maybe is not { } target ||
            !_panels.CanAccessEquipment(user, target) || !Accessible(user, target)) return;
        if (args.Operation is not ("query" or "release-all" or "attach" or "remove" or "escape" or "lock" or "unlock" or
            "color" or "color-held" or "pair" or "toggle" or "mode" or "pulse" or "leash" or "unleash")) return;
        if (args.Operation == "query") { _lastStates.Remove(user); _watchers[user] = target; SendState(user, target); return; }
        if (args.Operation == "release-all" && user == target)
        {
            foreach (var (attached, accessory) in Attached(target).ToArray()) { accessory.Locked = false; Detach(user, attached, accessory); }
            SendWatchers(target); return;
        }
        var held = _hands.GetActiveItem(user);
        var item = args.Operation is "attach" or "color-held" ? held : At(target, args.Slot);
        if (item is not { } uid || !Exists(uid)) return;
        if (args.Operation == "escape")
        {
            if (user != target) return;
            _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(10), new LustWishEscapeEvent(), user, uid)
            { NeedHand = false, RequireCanInteract = false, BreakOnMove = true, BreakOnDamage = true, BlockDuplicate = true });
            return;
        }
        Apply(user, target, uid, held, args.Operation, args.Slot, args.Value, false);
    }

    private void Request(EntityUid user, EntityUid target, EntityUid item, EntityUid? held, string operation, string slot, string value, bool remote, bool native = false)
    {
        if (!Alive(user) || !Alive(target) || !_blocker.CanConsciouslyPerformAction(user)) return;
        if (!(user == target && operation == "remove") && !_blocker.CanInteract(user, target)) return;
        if (remote && (held is not { } controllerUid || !TryComp<LustWishRemoteComponent>(controllerUid, out var controller) ||
            controller.Device != item || !RemoteInRange(user, target, controller.Range))) return;
        if (user == target) { Apply(user, target, item, held, operation, slot, value, remote, native); return; }
        if (Consent(user) == InteractionPanelConsent.No || Consent(target) == InteractionPanelConsent.No) return;
        if (Consent(target) == InteractionPanelConsent.Yes)
        { Apply(user, target, item, held, operation, slot, value, remote, native); return; }
        if (!TryComp<ActorComponent>(target, out var actor) || _blocked.Contains((target, user)) ||
            _pending.Values.Any(p => p.Target == target) || _askCooldown.GetValueOrDefault((user, target)) > _timing.CurTime) return;
        var token = Guid.NewGuid();
        _pending[token] = new(user, target, item, held, operation, slot, value, actor.PlayerSession, _timing.CurTime + TimeSpan.FromSeconds(20), remote, native);
        _askCooldown[(user, target)] = _timing.CurTime + TimeSpan.FromSeconds(5);
        RaiseNetworkEvent(new InteractionPanelAskEvent(token, Name(user), "", Loc.GetString("dl-lustwish-equipment-request"),
            $"{Loc.GetString("dl-lustwish-op-" + operation)}: {Name(item)}"), actor.PlayerSession);
    }

    private void OnAnswer(InteractionPanelAnswerEvent args, EntitySessionEventArgs session)
    {
        if (!_pending.TryGetValue(args.Token, out var pending) || pending.Recipient != session.SenderSession ||
            session.SenderSession.AttachedEntity != pending.Target) return;
        _pending.Remove(args.Token);
        if (args.Block) _blocked.Add((pending.Target, pending.User));
        if (!args.Accepted || pending.Expires <= _timing.CurTime || Consent(pending.Target) == InteractionPanelConsent.No ||
            Consent(pending.User) == InteractionPanelConsent.No) return;
        Apply(pending.User, pending.Target, pending.Item, pending.Held, pending.Operation, pending.Slot, pending.Value, pending.Remote, pending.Native);
    }

    private bool Apply(EntityUid user, EntityUid target, EntityUid item, EntityUid? held, string operation, string slot, string value, bool remote, bool native = false, bool protectionCompleted = false)
    {
        if (!Alive(user) || !Alive(target) || !Exists(item) || !_blocker.CanConsciouslyPerformAction(user) ||
            held != _hands.GetActiveItem(user)) return false;
        if (!(user == target && operation == "remove") && !_blocker.CanInteract(user, target)) return false;
        if (remote)
        {
            if (held is not { } remoteUid || !TryComp<LustWishRemoteComponent>(remoteUid, out var controller) || controller.Device != item ||
                !RemoteInRange(user, target, controller.Range)) return false;
        }
        else if (!Accessible(user, target) || !(native || _panels.CanAccessEquipment(user, target))) return false;
        if (operation == "color-held")
        {
            if (held != item || !LustWishEquipment.Colors.Contains(value) || !TryComp<LustWishPaletteComponent>(item, out var palette)) return false;
            palette.Color = Color.FromHex(value); Dirty(item, palette); return true;
        }
        if (!TryComp<LustWishAccessoryComponent>(item, out var accessory)) return false;
        if (operation == "attach")
        {
            if ((!native && held != item) || accessory.Wearer != null) return false;
            if (TryComp<BlueMoonProtectionComponent>(item, out var protection) && (protection.Wrapped || !_arousal.CanExposePart(target, accessory.Slot))) return false;
            if (protection != null && !protectionCompleted)
            {
                return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(4),
                    new BlueMoonProtectionAttachEvent(), item, target, item)
                { NeedHand = true, BreakOnMove = true, BreakOnDamage = true, BreakOnHandChange = true, BlockDuplicate = true });
            }
            if (accessory.InventorySlots.Length > 0)
            {
                var nativeSlot = native && accessory.InventorySlots.Contains(slot) ? slot : accessory.InventorySlots.FirstOrDefault(s => _inventory.HasSlot(target, s) && !_inventory.TryGetSlotEntity(target, s, out _));
                if (nativeSlot == null) return false;
                _approvedNative.Add((user, target, item));
                try { if (!_inventory.TryEquip(user, target, item, nativeSlot)) return false; }
                finally { _approvedNative.Remove((user, target, item)); }
            }
            else
            {
                if (!LustWishEquipment.Slots.Contains(accessory.Slot) || At(target, accessory.Slot) != null) return false;
                var container = _containers.EnsureContainer<ContainerSlot>(target, LustWishEquipment.ContainerPrefix + accessory.Slot);
                if (!_hands.TryDropIntoContainer(user, item, container)) return false;
                accessory.Wearer = target;
            }
        }
        else
        {
            if (accessory.Wearer != target || At(target, accessory.Slot) != item || accessory.Slot != slot) return false;
            switch (operation)
            {
                case "remove": if (accessory.Locked) return false; Detach(user, item, accessory); break;
                case "lock": case "unlock":
                    if (!accessory.Lockable || held is not { } key || !TryComp<LustWishKeyComponent>(key, out var keyComp)) return false;
                    if (keyComp.Code == Guid.Empty) keyComp.Code = Guid.NewGuid();
                    if (accessory.KeyCode == Guid.Empty && operation == "lock") accessory.KeyCode = keyComp.Code;
                    if (accessory.KeyCode != keyComp.Code) return false;
                    accessory.Locked = operation == "lock";
                    break;
                case "color":
                    if (!LustWishEquipment.Colors.Contains(value) || !TryComp<LustWishPaletteComponent>(item, out var tint)) return false;
                    tint.Color = Color.FromHex(value); Dirty(item, tint); break;
                case "pair":
                    if (!accessory.Electronic || held is not { } controllerId || !TryComp<LustWishRemoteComponent>(controllerId, out var controller)) return false;
                    controller.Device = item; break;
                case "toggle":
                    if (!accessory.Electronic && !accessory.Hypnosis) return false;
                    accessory.Active = value == "1"; accessory.ActiveController = user; break;
                case "mode":
                    if (!accessory.Electronic || !int.TryParse(value, out var intensity) || intensity is < 1 or > 3) return false;
                    accessory.Intensity = intensity; break;
                case "pulse":
                    if (!accessory.Shock || accessory.NextPulse > _timing.CurTime || !UsePower(item, 10)) return false;
                    accessory.NextPulse = _timing.CurTime + TimeSpan.FromSeconds(10);
                    _stun.TryStun(target, TimeSpan.FromSeconds(0.5), false); break;
                case "leash":
                    if (!(accessory.Slot == "neck" || accessory.InventorySlots.Contains("neck")) || held is not { } leash || !HasComp<LustWishLeashComponent>(leash) || accessory.LeashHolder != null) return false;
                    if (!_pulling.TryStartPull(user, target)) return false;
                    accessory.LeashHolder = user; accessory.Leash = leash; break;
                case "unleash": StopLeash(accessory); break;
                default: return false;
            }
        }
        Refresh(target);
        _logs.Add(LogType.Action, LogImpact.Low, $"LustWish {operation}: {ToPrettyString(user):user} -> {ToPrettyString(target):target}, item {ToPrettyString(item):item}");
        SendWatchers(target);
        return true;
    }

    private bool RemoteInRange(EntityUid user, EntityUid target, float range) =>
        Transform(user).Coordinates.TryDistance(EntityManager, Transform(target).Coordinates, out var distance) && distance <= Math.Clamp(range, 1, 64);
    private bool UsePower(EntityUid uid, float charge) => _cells.TryGetBatteryFromSlot(uid, out var cell, out var battery) && _battery.TryUseCharge(cell.Value, charge, battery);

    public void Refresh(EntityUid wearer)
    {
        var c = EnsureComp<LustWishWornEffectsComponent>(wearer);
        var attached = Attached(wearer).ToArray();
        var gear = attached.Select(a => a.Accessory).ToArray();
        foreach (var (item, accessory) in attached)
            if (accessory.InventorySlots.Length == 0 && TryComp<LustWishPaletteComponent>(item, out var palette))
            { _actions.AddAction(wearer, ref palette.ColorAction, "ActionLustWishChooseColor", item); Dirty(item, palette); }
        c.Items = attached.ToDictionary(a => a.Accessory.Slot, a => GetNetEntity(a.Uid));
        c.LeashHolder = gear.Select(a => a.LeashHolder).FirstOrDefault(h => h != null) is { } holder ? GetNetEntity(holder) : null;
        c.HandsBlocked = gear.Any(a => a.RestrictHands); c.LegsRestricted = gear.Any(a => a.RestrictLegs);
        c.Blind = gear.Any(a => a.Blind); c.Deaf = gear.Any(a => a.Deaf); c.Focused = gear.Any(a => a.Hypnosis && a.Active);
        Dirty(wearer, c); _effects.Refresh(wearer);
    }

    private void Detach(EntityUid user, EntityUid item, LustWishAccessoryComponent accessory)
    {
        if (accessory.Wearer is not { } wearer) return;
        StopLeash(accessory); accessory.Active = false; accessory.Wearer = null;
        if (accessory.InventorySlots.Length == 0 && TryComp<LustWishPaletteComponent>(item, out var palette)) _actions.RemoveAction(wearer, palette.ColorAction);
        if (accessory.InventorySlots.Length > 0)
        {
            _approvedNative.Add((user, wearer, item));
            try { _inventory.TryUnequip(user, wearer, accessory.Slot, force: true); }
            finally { _approvedNative.Remove((user, wearer, item)); }
        }
        else if (_containers.TryGetContainer(wearer, LustWishEquipment.ContainerPrefix + accessory.Slot, out var container)) _containers.Remove(item, container);
        Refresh(wearer);
        _hands.TryPickupAnyHand(user, item);
    }

    private void StopLeash(LustWishAccessoryComponent accessory)
    {
        if (accessory.Wearer is { } wearer && TryComp<PullableComponent>(wearer, out var pullable) && pullable.Puller == accessory.LeashHolder)
            _pulling.TryStopPull(wearer, pullable);
        accessory.LeashHolder = null; accessory.Leash = null;
        if (accessory.Wearer is { } body && Exists(body) && !Terminating(body)) Refresh(body);
    }

    private void OnAccessoryShutdown(EntityUid uid, LustWishAccessoryComponent component, ComponentShutdown args)
    {
        if (component.Wearer is not { } wearer) return;
        StopLeash(component); component.Wearer = null; component.Active = false;
        if (component.InventorySlots.Length == 0 && TryComp<LustWishPaletteComponent>(uid, out var palette)) _actions.RemoveAction(wearer, palette.ColorAction);
        if (Exists(wearer) && !Terminating(wearer)) Refresh(wearer);
    }

    private void OnEscape(EntityUid uid, LustWishWornEffectsComponent component, LustWishEscapeEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } item || !TryComp<LustWishAccessoryComponent>(item, out var accessory) || accessory.Wearer != uid) return;
        args.Handled = true; accessory.Locked = false;
        Detach(uid, item, accessory); SendWatchers(uid);
    }

    private void OnRemote(EntityUid uid, LustWishRemoteComponent component, UseInHandEvent args)
    {
        if (args.Handled || !_hands.IsHolding(args.User, uid, out _) || component.Device is not { } device ||
            !TryComp<LustWishAccessoryComponent>(device, out var accessory) || accessory.Wearer is not { } wearer || !RemoteInRange(args.User, wearer, component.Range)) return;
        args.Handled = true;
        Request(args.User, wearer, device, uid, "toggle", accessory.Slot, accessory.Active ? "0" : "1", true);
    }

    private void OnRemoteVerbs(EntityUid uid, LustWishRemoteComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !_hands.IsHolding(args.User, uid, out _) || component.Device is not { } device ||
            !TryComp<LustWishAccessoryComponent>(device, out var accessory) || accessory.Wearer is not { } wearer) return;
        foreach (var operation in accessory.Shock ? new[] { "pulse", "mode" } : new[] { "mode" })
        {
            var op = operation;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-lustwish-op-" + op),
                Act = () => Request(args.User, wearer, device, uid, op, accessory.Slot, (accessory.Intensity % 3 + 1).ToString(), true) });
        }
    }

    private void OnPaletteVerbs(EntityUid uid, LustWishPaletteComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || !_hands.IsHolding(args.User, uid, out _)) return;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-lustwish-color-action"),
            Act = () => OpenColorPicker(uid, component, args.User) });
    }

    private void OnCopyKey(EntityUid uid, LustWishKeyComponent component, InteractUsingEvent args)
    {
        if (args.Handled || component.Code != Guid.Empty || !_hands.IsHolding(args.User, args.Used, out _) ||
            !_interaction.InRangeAndAccessible(args.User, uid) || !TryComp<LustWishKeyComponent>(args.Used, out var original) || original.Code == Guid.Empty) return;
        component.Code = original.Code; args.Handled = true;
    }

    private void OnPack(EntityUid uid, LustWishPackComponent component, UseInHandEvent args)
    {
        if (args.Handled || component.Count <= 0 || !_hands.IsHolding(args.User, uid, out _)) return;
        var item = Spawn(component.Contents, Transform(args.User).Coordinates);
        component.Count--; args.Handled = true;
        _hands.TryPickupAnyHand(args.User, item);
        if (component.Count == 0) _popup.PopupEntity(Loc.GetString("dl-lustwish-pack-empty"), uid, args.User);
    }

    private void OnFurniture(EntityUid uid, LustWishFurnitureKitComponent component, UseInHandEvent args)
    {
        if (args.Handled || !_hands.IsHolding(args.User, uid, out _) || !_blocker.CanInteract(args.User, uid) || !CanBuild(args.User)) return;
        args.Handled = true;
        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User, TimeSpan.FromSeconds(3), new LustWishBuildEvent(), uid, used: uid)
            { BreakOnMove = true, BreakOnDamage = true, NeedHand = true, BlockDuplicate = true });
    }

    private bool CanBuild(EntityUid user) => Transform(user).GridUid != null && !_lookup.GetEntitiesInRange(user, 0.4f)
        .Any(e => e != user && Transform(e).Anchored && (HasComp<Content.Shared.Buckle.Components.StrapComponent>(e) ||
            TryComp<PhysicsComponent>(e, out var physics) && physics.CanCollide));

    private void OnBuild(EntityUid uid, LustWishFurnitureKitComponent component, LustWishBuildEvent args)
    {
        if (args.Cancelled || args.Handled || !_hands.IsHolding(args.User, uid, out _) || !CanBuild(args.User)) return;
        args.Handled = true;
        Spawn(component.Furniture, Transform(args.User).Coordinates);
        RemComp<LustWishFurnitureKitComponent>(uid); QueueDel(uid);
    }

    private void SendState(EntityUid user, EntityUid target)
    {
        if (!TryComp<ActorComponent>(user, out var actor)) return;
        var entries = Attached(target).Select(a => new LustWishGearEntry(a.Accessory.Slot, Name(a.Uid), a.Accessory.Locked,
            a.Accessory.Active, a.Accessory.Electronic, a.Accessory.Shock, a.Accessory.Hypnosis, a.Accessory.Intensity, HasComp<LustWishPaletteComponent>(a.Uid), a.Accessory.LeashHolder != null)).ToArray();
        var held = _hands.GetActiveItem(user);
        var state = new LustWishGearStateEvent(GetNetEntity(target), entries, held is { } item ? Name(item) : "",
            held is { } accessory && TryComp<LustWishAccessoryComponent>(accessory, out var c) ? c.Slot : "");
        if (_lastStates.TryGetValue(user, out var old) && old.Target == state.Target && old.HeldName == state.HeldName &&
            old.HeldSlot == state.HeldSlot && old.Entries.SequenceEqual(state.Entries)) return;
        _lastStates[user] = state;
        RaiseNetworkEvent(state, actor.PlayerSession);
    }
    private void SendWatchers(EntityUid target)
    {
        foreach (var (user, watched) in _watchers.ToArray()) if (watched == target && Accessible(user, target)) SendState(user, target);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(1);
        foreach (var (token, pending) in _pending.ToArray()) if (pending.Expires <= _timing.CurTime) _pending.Remove(token);
        foreach (var (user, target) in _watchers.ToArray())
        {
            if (!_panels.CanAccessEquipment(user, target) || !Accessible(user, target)) { _watchers.Remove(user); _lastStates.Remove(user); }
            else SendState(user, target);
        }
        var query = EntityQueryEnumerator<LustWishAccessoryComponent>();
        while (query.MoveNext(out var uid, out var accessory))
        {
            if (accessory.Wearer is not { } wearer) continue;
            if (!Exists(wearer)) { StopLeash(accessory); accessory.Wearer = null; accessory.Active = false; continue; }
            if (!Alive(wearer)) { StopLeash(accessory); accessory.Active = false; if (Exists(wearer) && !Terminating(wearer)) Refresh(wearer); continue; }
            if (accessory.LeashHolder is { } holder && (accessory.Leash is not { } leash || !Alive(holder) || !_hands.IsHolding(holder, leash, out _) ||
                !TryComp<PullableComponent>(wearer, out var pullable) || pullable.Puller != holder || !RemoteInRange(holder, wearer, 3))) StopLeash(accessory);
            if (accessory.Active && (accessory.ActiveController is { } controller && controller != wearer &&
                !Alive(controller) ||
                accessory.Electronic && !UsePower(uid, accessory.Intensity))) { accessory.Active = false; Refresh(wearer); }
            if (accessory.Active && accessory.Electronic && !accessory.Shock) _arousal.AdjustArousal(wearer, wearer, accessory.Intensity * 2);
        }
    }
}
