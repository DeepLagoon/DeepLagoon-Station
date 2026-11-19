using Content.Server.PowerCell;
using Content.Shared.Access.Systems;
using Content.Shared.Access.Components;
using Content.Server.Temperature.Components;
using Content.Shared.Atmos;
using Content.Server.Storage.EntitySystems;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared._Goobstation.Clothing.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.Actions;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Popups;
using Content.Shared.Storage;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Content.Shared.Wires;
using Robust.Shared.Containers;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Modsuits;

public sealed partial class ModSystem : EntitySystem
{
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private PowerCellSystem _power = default!;
    [Dependency] private AccessReaderSystem _modAccess = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedSealableClothingSystem _seal = default!;
    [Dependency] private ToggleableClothingSystem _deployment = default!;

    private TimeSpan _nextUpdate;
    private const float TickInterval = 0.25f;

    public override void Initialize()
    {
        SubscribeLocalEvent<ModControlComponent, ComponentInit>(OnControlComponentInit);
        SubscribeLocalEvent<ModControlComponent, MapInitEvent>(OnControlMapInit);
        SubscribeLocalEvent<ModControlComponent, InventoryRelayedEvent<DamageModifyEvent>>(OnDamageModify);
        SubscribeLocalEvent<ModControlComponent, InteractUsingEvent>(OnInstall, before: [typeof(StorageSystem)]);
        SubscribeLocalEvent<ModControlComponent, ActivateInWorldEvent>(OnActivateStorage);
        SubscribeLocalEvent<ModControlComponent, GetVerbsEvent<ActivationVerb>>(OnStorageVerbs);
        SubscribeLocalEvent<ModControlComponent, ClothingSealAttemptEvent>(OnSealAttempt);
        SubscribeLocalEvent<ModControlComponent, ClothingControlSealCompleteEvent>(OnSealComplete);
        SubscribeLocalEvent<ModControlComponent, EntInsertedIntoContainerMessage>(OnContainerInserted);
        SubscribeLocalEvent<ModControlComponent, EntRemovedFromContainerMessage>(OnContainerRemoved);
        SubscribeLocalEvent<ModLinkedToolComponent, ContainerGettingInsertedAttemptEvent>(OnToolInsertAttempt);
        SubscribeLocalEvent<ModModuleComponent, ComponentShutdown>(OnModuleShutdown);
        SubscribeLocalEvent<ModModuleComponent, StorageInteractAttemptEvent>(OnModuleStorageInteract);
        SubscribeLocalEvent<ModModuleComponent, BoundUIOpenedEvent>(OnModuleStorageUiOpened);
        SubscribeLocalEvent<ModModuleComponent, ContainerIsInsertingAttemptEvent>(OnModuleStorageInsert);
        InitializeLink();
        InitializeEmp();
        InitializeCloak();
        InitializeActions();
        InitializeHardlight();
        InitializeSourceModules();
        InitializeHats();
    }

    private void OnControlComponentInit(Entity<ModControlComponent> suit, ref ComponentInit args)
    {
        suit.Comp.Modules = _containers.EnsureContainer<Container>(suit, ModSuitContainers.Modules);
    }

    private void OnControlMapInit(Entity<ModControlComponent> suit, ref MapInitEvent args)
    {
        if (suit.Comp.Modules.Count > 0 || !TryComp<ModStartingModulesComponent>(suit, out var starting))
            return;

        foreach (var prototype in starting.Modules)
        {
            var uid = Spawn(prototype, Transform(suit).Coordinates);
            if (!TryComp<ModModuleComponent>(uid, out var module) || !Fits(suit, module) ||
                !_containers.Insert(uid, suit.Comp.Modules))
                QueueDel(uid);
        }
    }

    private bool HasCore(EntityUid uid)
    {
        return _containers.TryGetContainer(uid, ModControlComponent.CoreSlot, out var core) &&
               core.ContainedEntities.Count > 0;
    }

    private bool IsSealed(EntityUid uid)
    {
        return TryComp<SealableClothingControlComponent>(uid, out var control) && control.IsCurrentlySealed;
    }

    private bool CanService(EntityUid uid)
    {
        return TryComp<WiresPanelComponent>(uid, out var panel) && panel.Open &&
               TryComp<SealableClothingControlComponent>(uid, out var control) &&
               !control.IsInProcess && !control.IsCurrentlySealed;
    }

    private bool CanUse(EntityUid uid, EntityUid user)
    {
        return (!HasComp<AccessReaderComponent>(uid) || _modAccess.IsAllowed(user, uid)) && _blocker.CanInteract(user, uid) &&
               (IsWorn(uid, user) || _hands.IsHolding(user, uid) || !_containers.IsEntityInContainer(uid) && _interaction.InRangeUnobstructed(user, uid));
    }

    private bool IsWorn(EntityUid uid, EntityUid user)
    {
        if (!TryComp<ModLinkComponent>(uid, out var link) || link.Wearer != user)
            return false;

        return InSlot(link.Slot) || link.Slot == "neck" && (InSlot("neck2") || InSlot("neck3"));

        bool InSlot(string slot) => _inventory.TryGetSlotEntity(user, slot, out var equipped) && equipped == uid;
    }

    private int Complexity(Entity<ModControlComponent> suit)
    {
        var total = 0;
        foreach (var uid in suit.Comp.Modules.ContainedEntities)
        {
            if (TryComp<ModModuleComponent>(uid, out var module))
                total += module.Complexity;
        }

        return total;
    }

    private bool Fits(Entity<ModControlComponent> suit, ModModuleComponent module)
    {
        if (Complexity(suit) + module.Complexity > suit.Comp.Capacity)
            return false;

        foreach (var uid in suit.Comp.Modules.ContainedEntities)
        {
            if (TryComp<ModModuleComponent>(uid, out var installed) && installed.Family == module.Family)
                return false;
        }

        return true;
    }

    private void OnInstall(Entity<ModControlComponent> suit, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<ModModuleComponent>(args.Used, out var module))
            return;

        args.Handled = true;
        if (!CanUse(suit, args.User) || !CanService(suit))
        {
            Notify(suit, args.User, "mod-service-required");
            return;
        }

        if (module.Host != null || !Fits(suit, module))
        {
            Notify(suit, args.User, "mod-module-incompatible");
            return;
        }

        if (_hands.TryDropIntoContainer(args.User, args.Used, suit.Comp.Modules))
            RefreshUi(suit);
    }

    private void OnStorageVerbs(Entity<ModControlComponent> suit, ref GetVerbsEvent<ActivationVerb> args)
    {
        var user = args.User;
        if (!args.CanAccess || !args.CanInteract || !CanUse(suit, user))
            return;

        foreach (var item in suit.Comp.Modules.ContainedEntities)
        {
            if (!TryComp<ModModuleComponent>(item, out var module) || module.Mode != ModModuleMode.Storage)
                continue;

            args.Verbs.Add(new ActivationVerb
            {
                Text = Loc.GetString("mod-open-storage", ("name", Name(item))),
                Act = () =>
                {
                    if (CanUse(suit, user) && suit.Comp.Modules.Contains(item))
                        OpenModuleStorage(item, user);
                },
            });
        }
    }

    private void OnSealAttempt(Entity<ModControlComponent> suit, ref ClothingSealAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (!CanUse(suit, args.User))
        {
            args.Cancel();
            return;
        }

        if (suit.Comp.Deployment != null)
        {
            args.Cancel();
            return;
        }

        if (!IsSealed(suit) && !HasCore(suit))
        {
            args.Cancel();
            Notify(suit, args.User, "mod-core-missing");
        }
    }

    private void OnSealComplete(Entity<ModControlComponent> suit, ref ClothingControlSealCompleteEvent args)
    {
        if (!args.IsSealed)
        {
            StopModules(suit);
            if (TryComp<ModLinkComponent>(suit, out var device))
                EndCall((suit.Owner, device));
        }

        if (TryComp<ModLinkComponent>(suit, out var link) && link.Wearer is {} wearer)
        {
            RefreshHardlight(suit, wearer);
            RefreshSuitActionIcons(suit);
        }
        RefreshUi(suit);
    }

    private void OnContainerInserted(Entity<ModControlComponent> suit, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == ModSuitContainers.Modules && TryComp<ModModuleComponent>(args.Entity, out var module))
            module.Host = suit;
    }

    private void OnContainerRemoved(Entity<ModControlComponent> suit, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container != suit.Comp.Modules || !TryComp<ModModuleComponent>(args.Entity, out var module))
            return;

        StopModule((args.Entity, module));
        UnpinModule(module);
        if (suit.Comp.SelectedModule == args.Entity)
            suit.Comp.SelectedModule = null;
        module.Host = null;
        _ui.CloseUi(args.Entity, StorageComponent.StorageUiKey.Key);
        RefreshUi(suit);
    }

    private void OnModuleShutdown(Entity<ModModuleComponent> module, ref ComponentShutdown args)
    {
        StopModule(module);
        UnpinModule(module.Comp);
    }

    private void Notify(EntityUid device, EntityUid user, LocId message)
    {
        _popup.PopupEntity(Loc.GetString(message), device, user);
    }

    private void Open(Entity<ModLinkComponent> device, EntityUid user)
    {
        if (!CanUse(device, user) || !HasComp<ModControlComponent>(device) && !IsWorn(device, user))
            return;

        _ui.TryOpenUi(device.Owner, ModUiKey.Key, user);
        RefreshUi(device);
    }

    private void OnUiMessage(Entity<ModLinkComponent> device, ref ModUiMessage args)
    {
        var user = args.Actor;
        if (!Equals(args.UiKey, ModUiKey.Key) || !CanUse(device, user))
            return;

        if (args.Operation == ModOperation.ToggleSuit && IsWorn(device, user) &&
            TryComp<ModControlComponent>(device, out var control))
            ToggleSuit((device.Owner, control), user);
        else if (args.Operation == ModOperation.OpenHardlightStyle && IsWorn(device, user) &&
                 TryComp<ModControlComponent>(device, out var styleControl))
            OpenHardlightEditor((device.Owner, styleControl), user);
        else if (args.Operation is ModOperation.ToggleModule or ModOperation.RemoveModule or ModOperation.ConfigureModule or ModOperation.PinModule)
        {
            if (!TryComp<ModControlComponent>(device, out var suit) ||
                !TryGetEntity(args.Target, out var target) ||
                !suit.Modules.Contains(target.Value) ||
                !TryComp<ModModuleComponent>(target, out var module))
                return;

            if (args.Operation == ModOperation.RemoveModule)
            {
                if (!CanService(device) || !module.Removable)
                    return;

                if (TryComp<StorageComponent>(target, out var storage) && storage.Container.Count > 0)
                {
                    Notify(device, user, "mod-empty-storage");
                    return;
                }

                if (_containers.Remove(target.Value, suit.Modules))
                    _hands.TryPickupAnyHand(user, target.Value);
            }
            else if (args.Operation == ModOperation.ConfigureModule)
            {
                if (IsWorn(device, user) && module.Mode == ModModuleMode.ThermalRegulator &&
                    args.Text is { Length: <= 2 } input && int.TryParse(input, out var temperature) &&
                    temperature is >= 20 and <= 45)
                    module.TargetTemperature = temperature;
            }
            else if (args.Operation == ModOperation.PinModule && IsWorn(device, user))
                PinModule((target.Value, module), user);
            else if (args.Operation == ModOperation.ToggleModule && IsWorn(device, user))
                UseSelectedModule((device.Owner, suit), (target.Value, module), user);
            else if (args.Operation == ModOperation.ToggleModule && module.Mode == ModModuleMode.Storage)
                OpenModuleStorage(target.Value, user);
        }
        else if (IsWorn(device, user))
            HandleLinkMessage(device, args);

        RefreshUi(device);
    }

    private void ToggleSuit(Entity<ModControlComponent> suit, EntityUid user)
    {
        if (IsSuitBusy(suit) || !TryComp<SealableClothingControlComponent>(suit, out var seal))
            return;

        if (!seal.IsCurrentlySealed && _deployment.GetAttachedToggleStatus(suit) == ToggleableClothingAttachedStatus.NoneToggled)
        {
            Notify(suit, user, "mod-deploy-first");
            return;
        }

        _seal.TryStartSealToggleProcess((suit.Owner, seal), user);
    }

    public override void Update(float frameTime)
    {
        UpdateSourceModules();
        UpdateCloakReveals();
        if (_timing.CurTime < _nextUpdate)
            return;

        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(TickInterval);
        var devices = EntityQueryEnumerator<ModLinkComponent>();
        while (devices.MoveNext(out var uid, out var link))
        {
            if (link.Wearer is {} user)
            {
                if (TryComp<ModControlComponent>(uid, out var suit))
                {
                    foreach (var item in suit.Modules.ContainedEntities)
                    {
                        if (!TryComp<ModModuleComponent>(item, out var module) || !module.StartPinned) continue;
                        PinModule((item, module), user);
                        module.StartPinned = false;
                    }
                    if (TryComp<SealableClothingControlComponent>(uid, out var seal) &&
                        (seal.IsCurrentlySealed || seal.IsInProcess) &&
                        (!HasCore(uid) || !_power.HasCharge(uid, 0.01f)))
                    {
                        _seal.ResetSeal((uid, seal));
                        Notify(uid, user, "mod-power-depleted");
                    }

                    UpdateModules((uid, suit), user);
                    RefreshHardlight((uid, suit), user);
                    RefreshSuitActionIcons((uid, suit));
                }

                if (link.Peer != null)
                    UpdateLink((uid, link));
            }

            if (_ui.IsUiOpen(uid, ModUiKey.Key))
                RefreshUi(uid);
        }
    }

    private void RefreshUi(EntityUid uid)
    {
        if (!_ui.IsUiOpen(uid, ModUiKey.Key) || !TryComp<ModLinkComponent>(uid, out var link))
            return;

        var charge = _power.TryGetBatteryFromSlot(uid, out var cell, out var battery) && battery.MaxCharge > 0
            ? (int) MathF.Round(100 * battery.CurrentCharge / battery.MaxCharge)
            : -1;

        var isSuit = TryComp<ModControlComponent>(uid, out var suit);
        var modules = new List<ModModuleEntry>();
        var parts = new List<ModHardwarePart>();
        int? bodyTemperature = null;
        if (isSuit)
        {
            if (TryComp<ToggleableClothingComponent>(uid, out var clothing))
            {
                foreach (var part in GetSuitParts(clothing))
                {
                    if (Exists(part))
                        parts.Add(new(clothing.ClothingUids[part], Name(part), link.Wearer is {} wearerId &&
                            IsPartDeployed(clothing, wearerId, part)));
                }
            }
            foreach (var item in suit!.Modules.ContainedEntities)
            {
                if (!TryComp<ModModuleComponent>(item, out var module))
                    continue;

                modules.Add(new(GetNetEntity(item), Name(item), MetaData(item).EntityDescription, module.Complexity,
                    module.Active, module.Mode != ModModuleMode.Passive, module.Mode != ModModuleMode.Storage,
                    module.PinnedAction != null, module.UseCharge,
                    (int) Math.Max(0, Math.Ceiling((module.NextUse - _timing.CurTime).TotalSeconds)), (float) module.UseDelay.TotalSeconds,
                    module.Mode == ModModuleMode.ThermalRegulator ? module.TargetTemperature : null));
                if (module.Mode == ModModuleMode.ThermalRegulator && module.Active &&
                    TryComp<TemperatureComponent>(link.Wearer, out var temperature))
                    bodyTemperature = (int) MathF.Round(temperature.CurrentTemperature - Atmospherics.T0C);
            }
        }

        var call = link.Peer == null ? ModCallState.Idle :
            link.Connected ? ModCallState.Connected :
            link.Incoming ? ModCallState.Incoming : ModCallState.Dialing;

        var peer = TryComp<ModLinkComponent>(link.Peer, out var peerLink) ? DisplayName(peerLink) : string.Empty;
        var contacts = link.Wearer != null && call == ModCallState.Idle ? Contacts((uid, link)) : [];
        var frequencyTool = link.Wearer is {} wearer && HasMultitool(wearer);
        // Contacts and frequency controls belong to the wearer, so ground service can only view the module list.
        if (link.Wearer == null || !_ui.IsUiOpen(uid, ModUiKey.Key, link.Wearer.Value))
        {
            contacts = [];
            frequencyTool = false;
        }

        var busy = suit != null && IsSuitBusy((uid, suit));
        var details = new ModPanelDetails(Name(uid),
            link.Wearer is {} user ? Name(user) : string.Empty,
            cell is {} cellUid ? Name(cellUid) : string.Empty,
            suit?.SelectedModule is {} selected && Exists(selected) ? Name(selected) : string.Empty,
            bodyTemperature);
        var state = new ModUiState(charge, isSuit, IsSealed(uid), busy, link.Wearer != null, CanService(uid), suit?.Capacity ?? 0,
            isSuit ? Complexity((uid, suit!)) : 0, modules.ToArray(), link.Number, link.Label, link.Frequency, frequencyTool,
            contacts, call, peer, parts.ToArray(), details);
        SetUiIfChanged(uid, link, state);
    }

    private void SetUiIfChanged(EntityUid uid, ModLinkComponent link, ModUiState state)
    {
        var old = link.LastState;
        if (old != null && old.Charge == state.Charge && old.Active == state.Active && old.Service == state.Service &&
            old.Busy == state.Busy && old.WearableControls == state.WearableControls &&
            old.Capacity == state.Capacity && old.Complexity == state.Complexity && old.Number == state.Number &&
            old.Label == state.Label && old.Frequency == state.Frequency && old.CanSetFrequency == state.CanSetFrequency &&
            old.Call == state.Call && old.Peer == state.Peer && old.Details == state.Details &&
            old.Parts.AsSpan().SequenceEqual(state.Parts) &&
            old.Modules.AsSpan().SequenceEqual(state.Modules) && old.Contacts.AsSpan().SequenceEqual(state.Contacts))
            return;

        link.LastState = state;
        _ui.SetUiState(uid, ModUiKey.Key, state);
    }
}
