using System.Linq;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._Goobstation.Clothing;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared.Actions;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.DoAfter;
using Robust.Shared.Audio;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Modsuits;

public sealed partial class ModSystem
{
    [Dependency] private SharedDoAfterSystem _modDoAfter = default!;
    [Dependency] private MetaDataSystem _metadata = default!;

    private static readonly TimeSpan DeploymentDelay = TimeSpan.FromSeconds(0.9);
    private static readonly SoundSpecifier DeploySound = new SoundPathSpecifier("/Audio/Mecha/mechmove03.ogg");
    private static readonly string[] DeployOrder = ["head", "outerClothing", "gloves", "shoes"];

    private void InitializeActions()
    {
        SubscribeLocalEvent<ModControlComponent, ModSuitActionEvent>(OnSuitAction);
        SubscribeLocalEvent<ModControlComponent, ModDeployStepEvent>(OnDeployStep);
        SubscribeLocalEvent<ModControlComponent, ModRadialSelection>(OnRadialSelection);
        SubscribeLocalEvent<ModModuleComponent, ModPinnedModuleEvent>(OnPinnedModule);
    }

    private void AddSuitActions(ModControlComponent control, ModLinkComponent link, GetItemActionsEvent args)
    {
        args.AddAction(ref control.DeployAction, "ActionModDeploy");
        args.AddAction(ref control.ActivateAction, "ActionModActivate");
        args.AddAction(ref control.ModuleAction, "ActionModModule");
        args.AddAction(ref link.Action, "ActionModPanel");
        args.AddAction(ref control.HardlightAction, "ActionModHardlight");
        args.AddAction(ref control.HardlightStyleAction, "ActionModHardlightStyle");
    }

    private void OnSuitAction(Entity<ModControlComponent> suit, ref ModSuitActionEvent args)
    {
        var user = args.Performer;
        if (args.Handled || !IsWorn(suit, user) || !_blocker.CanInteract(user, suit))
            return;

        args.Handled = true;
        switch (args.Kind)
        {
            case ModActionKind.Deploy:
                if (args.Alternate)
                    StartDeployment(suit, user);
                else
                    OpenRadial(suit, user, ModRadialMode.Parts);
                break;
            case ModActionKind.Activate:
                if (args.Alternate)
                    StartDeployment(suit, user);
                else
                    ToggleSuit(suit, user);
                break;
            case ModActionKind.Module:
                OpenRadial(suit, user, args.Alternate ? ModRadialMode.PinModule : ModRadialMode.Module);
                break;
            case ModActionKind.Hardlight:
                if (args.Alternate)
                    ToggleAllHardlight(suit, user);
                else
                    OpenRadial(suit, user, ModRadialMode.Hardlight);
                break;
            case ModActionKind.HardlightStyle:
                OpenHardlightEditor(suit, user);
                break;
        }
    }

    private void OpenRadial(Entity<ModControlComponent> suit, EntityUid user, ModRadialMode mode)
    {
        suit.Comp.RadialMode = mode;
        var choices = GetRadialChoices(suit, user);
        if (choices.Count == 0)
            return;

        _ui.TryOpenUi(suit.Owner, ModRadialKey.Key, user);
        _ui.SetUiState(suit.Owner, ModRadialKey.Key, new ModRadialState(choices.ToArray()));
    }

    private List<ModRadialChoice> GetRadialChoices(Entity<ModControlComponent> suit, EntityUid user)
    {
        var choices = new List<ModRadialChoice>();
        if (suit.Comp.RadialMode == ModRadialMode.Parts)
        {
            if (!TryComp<ToggleableClothingComponent>(suit, out var deployment))
                return choices;

            foreach (var part in GetSuitParts(deployment))
            {
                if (MetaData(part).EntityPrototype is not {} proto)
                    continue;

                var deployed = IsPartDeployed(deployment, user, part);
                var label = Loc.GetString(deployed ? "mod-radial-retract" : "mod-radial-deploy", ("part", Name(part)));
                choices.Add(new(GetNetEntity(part).Id, label, new SpriteSpecifier.EntityPrototype(proto.ID), deployed));
            }
        }
        else if (suit.Comp.RadialMode == ModRadialMode.Hardlight)
            AddHardlightChoices(suit, user, choices);
        else
        {
            foreach (var item in suit.Comp.Modules.ContainedEntities)
            {
                if (!TryComp<ModModuleComponent>(item, out var comp) || comp.Mode == ModModuleMode.Passive ||
                    MetaData(item).EntityPrototype is not {} proto)
                    continue;

                var pinned = comp.PinnedAction != null;
                var label = suit.Comp.RadialMode == ModRadialMode.PinModule
                    ? Loc.GetString(pinned ? "mod-radial-pinned" : "mod-radial-pin", ("module", Name(item)))
                    : Name(item);
                choices.Add(new(GetNetEntity(item).Id, label, new SpriteSpecifier.EntityPrototype(proto.ID),
                    suit.Comp.RadialMode == ModRadialMode.PinModule ? pinned : comp.Active));
            }
        }

        return choices;
    }

    private void OnRadialSelection(Entity<ModControlComponent> suit, ref ModRadialSelection args)
    {
        var user = args.Actor;
        if (!Equals(args.UiKey, ModRadialKey.Key) || !IsWorn(suit, user) || !_blocker.CanInteract(user, suit))
            return;

        var choice = args.Choice;
        if (suit.Comp.RadialMode == ModRadialMode.Hardlight)
        {
            SelectHardlight(suit, user, choice);
            OpenRadial(suit, user, ModRadialMode.Hardlight);
            return;
        }

        if (!TryGetEntity(new NetEntity(choice), out var item))
            return;

        if (suit.Comp.RadialMode == ModRadialMode.Parts)
        {
            StartDeployment(suit, user, item.Value);
            return;
        }

        if (!suit.Comp.Modules.Contains(item.Value) || !TryComp<ModModuleComponent>(item, out var module))
            return;

        if (suit.Comp.RadialMode == ModRadialMode.PinModule)
            PinModule((item.Value, module), user);
        else
            UseSelectedModule(suit, (item.Value, module), user);

        _ui.CloseUi(suit.Owner, ModRadialKey.Key, user);
        RefreshUi(suit);
    }

    private void PinModule(Entity<ModModuleComponent> module, EntityUid user)
    {
        if (module.Comp.PinnedAction != null || module.Comp.Mode == ModModuleMode.Passive)
            return;

        if (!_actions.AddAction(user, ref module.Comp.PinnedAction, out var action, "ActionModPinnedModule", module))
            return;

        _actions.SetEntityIcon(module.Comp.PinnedAction.Value, module, action);
        _metadata.SetEntityName(module.Comp.PinnedAction.Value, Name(module));
        _metadata.SetEntityDescription(module.Comp.PinnedAction.Value,
            Loc.GetString("mod-action-unpin", ("description", MetaData(module).EntityDescription)));
    }

    private void UnpinModule(ModModuleComponent module)
    {
        if (module.PinnedAction is not {} action)
            return;

        module.PinnedAction = null;
        _actions.RemoveAction(action);
        QueueDel(action);
    }

    private void OnPinnedModule(Entity<ModModuleComponent> module, ref ModPinnedModuleEvent args)
    {
        if (args.Handled || module.Comp.Host is not {} host || !IsWorn(host, args.Performer) ||
            !TryComp<ModControlComponent>(host, out var suit) || !suit.Modules.Contains(module))
            return;

        args.Handled = true;
        if (args.Alternate)
            UnpinModule(module.Comp);
        else
            UseSelectedModule((host, suit), module, args.Performer);

        RefreshUi(host);
    }

    private void UseSelectedModule(Entity<ModControlComponent> suit, Entity<ModModuleComponent> module, EntityUid user)
    {
        if (module.Comp.Mode == ModModuleMode.Passive)
            return;

        suit.Comp.SelectedModule = module.Owner;
        if (module.Comp.Mode == ModModuleMode.Storage)
            OpenModuleStorage(module, user);
        else if (IsSealed(suit) && HasCore(suit) && !IsSuitBusy(suit))
            UseModule(suit, module, user);
        else
            Notify(suit, user, "mod-activate-first");
    }

    private bool IsSuitBusy(Entity<ModControlComponent> suit)
    {
        return suit.Comp.Deployment != null ||
               TryComp<SealableClothingControlComponent>(suit, out var seal) && seal.IsInProcess;
    }

    private static IEnumerable<EntityUid> GetSuitParts(ToggleableClothingComponent deployment)
    {
        foreach (var slot in DeployOrder)
        {
            foreach (var (part, partSlot) in deployment.ClothingUids)
            {
                if (partSlot == slot)
                    yield return part;
            }
        }
    }

    private bool IsPartDeployed(ToggleableClothingComponent deployment, EntityUid user, EntityUid part)
    {
        return ToggleableClothingSystem.TryGetAttachedSlot(deployment, part, out var slot) &&
               _inventory.TryGetSlotEntity(user, slot, out var equipped) && equipped == part;
    }

    private void StartDeployment(Entity<ModControlComponent> suit, EntityUid user, EntityUid? part = null)
    {
        if (IsSuitBusy(suit) || !IsWorn(suit, user) ||
            !TryComp<ToggleableClothingComponent>(suit, out var deployment))
            return;

        var parts = GetSuitParts(deployment).Where(Exists).ToArray();
        if (parts.Length == 0 || part is {} specific && !parts.Contains(specific))
            return;

        var deploy = part is {} selected
            ? !IsPartDeployed(deployment, user, selected)
            : parts.Any(piece => !IsPartDeployed(deployment, user, piece));

        suit.Comp.Deployment = new ModDeployment(user, deploy, part != null,
            new Queue<EntityUid>(part is {} single ? [single] :
                parts.Where(piece => IsPartDeployed(deployment, user, piece) != deploy)));
        _ui.CloseUi(suit.Owner, ModRadialKey.Key, user);
        NextDeploymentStep(suit);
    }

    private void NextDeploymentStep(Entity<ModControlComponent> suit)
    {
        if (suit.Comp.Deployment is not {} cycle)
            return;

        if (cycle.Parts.TryDequeue(out var part))
        {
            // The controller owns the sequence so walking and changing hands do not interrupt it.
            var step = new DoAfterArgs(EntityManager, suit, DeploymentDelay, new ModDeployStepEvent(), suit,
                target: part, showTo: cycle.User)
            {
                NeedHand = false,
                RequireCanInteract = false,
            };
            if (_modDoAfter.TryStartDoAfter(step, out var id))
            {
                cycle.Step = id;
                return;
            }
        }

        suit.Comp.Deployment = null;
        if (cycle.Reopen && IsWorn(suit, cycle.User) && !_ui.IsUiOpen(suit.Owner, ModRadialKey.Key, cycle.User) &&
            TryComp<ToggleableClothingComponent>(suit, out var deployment) &&
            GetSuitParts(deployment).Any(piece => IsPartDeployed(deployment, cycle.User, piece) != cycle.Deploy))
            OpenRadial(suit, cycle.User, ModRadialMode.Parts);

        RefreshUi(suit);
    }

    private void OnDeployStep(Entity<ModControlComponent> suit, ref ModDeployStepEvent args)
    {
        if (suit.Comp.Deployment is not {} cycle || cycle.Step != args.DoAfter.Id)
            return;

        cycle.Step = null;
        if (args.Cancelled || args.Target is not {} part || !IsWorn(suit, cycle.User) ||
            !_blocker.CanInteract(cycle.User, suit) || !TryComp<ToggleableClothingComponent>(suit, out var deployment))
        {
            suit.Comp.Deployment = null;
            return;
        }

        if (_deployment.SetPartDeployed(cycle.User, (suit.Owner, deployment), part, cycle.Deploy))
        {
            if (TryComp<SealableClothingComponent>(part, out var seal))
                _seal.SetPartSeal((part, seal), cycle.Deploy && IsSealed(suit));

            _audio.PlayPvs(DeploySound, suit.Owner);
            RefreshHardlight(suit, cycle.User);
            RefreshSuitActionIcons(suit);
        }

        NextDeploymentStep(suit);
    }

    private void RefreshSuitActionIcons(Entity<ModControlComponent> suit)
    {
        var deployed = _deployment.GetAttachedToggleStatus(suit) == ToggleableClothingAttachedStatus.AllToggled;
        var active = IsSealed(suit);
        _actions.SetToggled(suit.Comp.DeployAction, deployed);
        _actions.SetToggled(suit.Comp.ActivateAction, active);
        _modAppearance.SetData(suit.Owner, SealableClothingVisuals.Sealed, active && deployed);
        _actions.SetToggled(suit.Comp.HardlightAction, suit.Comp.HardlightParts != ModHardlightParts.None);
    }
}

public enum ModRadialMode : byte
{
    Parts,
    Module,
    PinModule,
    Hardlight,
}

public sealed record ModDeployment(EntityUid User, bool Deploy, bool Reopen, Queue<EntityUid> Parts)
{
    public DoAfterId? Step;
}
