using Content.Server._NF.Construction.Components;
using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.Stack;
using Content.Server.Storage.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.Construction.Components;
using Content.Shared.Exchanger;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.Utility;
using Content.Shared.Wires;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Content.Shared.Stacks;
using Content.Shared.Construction.Prototypes;

namespace Content.Server._NF.Construction;

public sealed partial class PartExchangerSystem : EntitySystem
{
    [Dependency] private ConstructionSystem _construction = default!;
    [Dependency] private MachineFrameSystem _frames = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private StorageSystem _storage = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ExchangeTrailSystem _trails = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<PartExchangerComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<PartExchangerComponent, ExchangerDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<PartExchangerComponent, DoAfterAttemptEvent<ExchangerDoAfterEvent>>(OnExchangeAttempt);
    }

    private readonly record struct ExchangePart(EntityUid Item, int Rating, StackComponent? Stack, bool Installed)
    {
        public int Quantity => Stack?.Count ?? 1;
    }

    private void OnDoAfter(EntityUid uid, PartExchangerComponent component, DoAfterEvent args)
    {
        if (args.Cancelled || args.Args.Target is not { } target || !CanService(component, args.User, target))
        {
            component.AudioStream = _audio.Stop(component.AudioStream);
            return;
        }

        if (!args.Handled)
            args.Handled = Exchange(uid, target);
    }

    private void OnExchangeAttempt(EntityUid uid, PartExchangerComponent component, DoAfterAttemptEvent<ExchangerDoAfterEvent> args)
    {
        if (args.DoAfter.Args.Target is not { } target || !CanService(component, args.DoAfter.Args.User, target))
            args.Cancel();
    }

    private bool Exchange(EntityUid tool, EntityUid target)
    {
        if (!TryComp<StorageComponent>(tool, out var storage) || storage.Container == null)
            return false;

        if (TryComp<MachineComponent>(target, out var machine) &&
            TryComp<MachineBoardComponent>(machine.BoardContainer.ContainedEntities.FirstOrNull(), out var board))
        {
            var upgraded = InstallParts(tool, storage, machine.PartContainer, board.Requirements);
            if (upgraded && !TerminatingOrDeleted(target))
                _construction.RefreshParts(target, machine);

            return upgraded;
        }

        if (!TryComp<MachineFrameComponent>(target, out var frame) ||
            !TryComp<MachineBoardComponent>(frame.BoardContainer.ContainedEntities.FirstOrNull(), out board))
        {
            return false;
        }

        var completed = InstallParts(tool, storage, frame.PartContainer, board.Requirements);
        if (completed && !TerminatingOrDeleted(target))
            _frames.RegenerateProgress(frame);

        return completed;
    }

    private bool InstallParts(EntityUid tool, StorageComponent storage, BaseContainer destination,
        Dictionary<ProtoId<MachinePartPrototype>, int> requirements)
    {
        var byType = new Dictionary<ProtoId<MachinePartPrototype>, List<ExchangePart>>();
        foreach (var item in storage.Container.ContainedEntities)
        {
            if (TerminatingOrDeleted(item) || !_construction.GetMachinePartState(item, out var part) ||
                !requirements.ContainsKey(part.Part.PartType))
            {
                continue;
            }

            if (!byType.TryGetValue(part.Part.PartType, out var candidates))
                byType[part.Part.PartType] = candidates = new List<ExchangePart>();

            candidates.Add(new ExchangePart(item, part.Part.Rating, part.Stack, Installed: false));
        }

        if (byType.Count == 0)
            return false;

        foreach (var item in destination.ContainedEntities)
        {
            if (!TerminatingOrDeleted(item) && _construction.GetMachinePartState(item, out var part) &&
                byType.TryGetValue(part.Part.PartType, out var candidates))
            {
                candidates.Add(new ExchangePart(item, part.Part.Rating, part.Stack, Installed: true));
            }
        }

        var upgraded = false;
        var removed = new List<EntityUid>();
        foreach (var (type, candidates) in byType)
        {
            candidates.Sort(static (a, b) => a.Rating == b.Rating
                ? b.Installed.CompareTo(a.Installed)
                : b.Rating.CompareTo(a.Rating));

            var capacity = requirements[type];
            if (!WouldInstallNewPart(candidates, capacity))
                continue;

            if (TerminatingOrDeleted(destination.Owner))
                break;

            removed.Clear();
            var canExchange = true;
            foreach (var candidate in candidates)
            {
                if (!candidate.Installed)
                    continue;

                if (TerminatingOrDeleted(destination.Owner) || !_container.Remove(candidate.Item, destination))
                {
                    canExchange = false;
                    break;
                }

                removed.Add(candidate.Item);
            }

            if (!canExchange)
            {
                // Restore the machine before touching any inventory when removal is blocked.
                foreach (var item in removed)
                {
                    if (!TerminatingOrDeleted(destination.Owner) && !TerminatingOrDeleted(item))
                        _container.Insert(item, destination, force: true);
                }

                continue;
            }

            foreach (var candidate in candidates)
            {
                if (capacity <= 0 || TerminatingOrDeleted(destination.Owner))
                    break;

                var inserted = InstallPart(candidate, capacity, destination);
                capacity -= inserted;
                upgraded |= inserted > 0 && !candidate.Installed;
            }

            foreach (var item in removed)
            {
                // The old part is already at the machine if the RPED has no room to take it.
                if (!destination.Contains(item) && !TerminatingOrDeleted(tool) && !TerminatingOrDeleted(item))
                    _storage.Insert(tool, item, out _, playSound: false);
            }
        }

        return upgraded;
    }

    private static bool WouldInstallNewPart(List<ExchangePart> candidates, int capacity)
    {
        foreach (var candidate in candidates)
        {
            if (capacity <= 0)
                break;

            if (candidate.Quantity <= 0)
                continue;

            if (!candidate.Installed)
                return true;

            capacity -= candidate.Quantity;
        }

        return false;
    }

    private int InstallPart(ExchangePart candidate, int capacity, BaseContainer destination)
    {
        var count = candidate.Quantity;
        if (count <= 0 || TerminatingOrDeleted(candidate.Item))
            return 0;

        if (count <= capacity)
            return _container.Insert(candidate.Item, destination, force: candidate.Installed) ? count : 0;

        var split = _stack.Split(candidate.Item, capacity, Transform(destination.Owner).Coordinates, candidate.Stack);
        if (split is not { } item)
            return 0;

        if (!TerminatingOrDeleted(destination.Owner) && _container.Insert(item, destination, force: candidate.Installed))
            return capacity;

        if (!TerminatingOrDeleted(candidate.Item) && !TerminatingOrDeleted(item) && candidate.Stack != null)
        {
            _stack.SetCount(candidate.Item, count, candidate.Stack);
            QueueDel(item);
        }

        return 0;
    }

    private void OnAfterInteract(EntityUid uid, PartExchangerComponent component, AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } target)
            return;

        if (!IsServiceable(target))
            return;

        if (component.RemoteRange > 0)
        {
            if (!CanService(component, args.User, target) || !Exchange(uid, target))
                return;

            _audio.PlayPvs(component.ExchangeSound, uid);
            _trails.Show(args.User, target, TimeSpan.FromSeconds(1));
            args.Handled = true;
            return;
        }

        if (component.DoDistanceCheck && !args.CanReach)
            return;

        if (TryComp<WiresPanelComponent>(target, out var panel) && !panel.Open)
        {
            _popup.PopupEntity(Loc.GetString("construction-step-condition-wire-panel-open"),
                args.Target.Value);
            return;
        }

        if (!_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User, component.ExchangeDuration,
                new ExchangerDoAfterEvent(), uid, target: target, used: uid)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            NeedHand = true,
            AttemptFrequency = AttemptFrequency.EveryTick,
        }))
            return;

        args.Handled = true;
        component.AudioStream = _audio.PlayPvs(component.ExchangeSound, uid)?.Entity;
    }

    private bool CanService(PartExchangerComponent exchanger, EntityUid user, EntityUid target)
    {
        if (TerminatingOrDeleted(target) || TerminatingOrDeleted(user) || !IsServiceable(target))
            return false;

        if (exchanger.RemoteRange > 0)
            return _examine.InRangeUnOccluded(user, _transform.GetMapCoordinates(target), exchanger.RemoteRange);

        return (!exchanger.DoDistanceCheck || _interaction.InRangeUnobstructed(user, target))
               && (!TryComp<WiresPanelComponent>(target, out var panel) || panel.Open);
    }

    private bool IsServiceable(EntityUid target)
    {
        if (TryComp<MachineComponent>(target, out var machine))
            return HasComp<MachineBoardComponent>(machine.BoardContainer.ContainedEntities.FirstOrNull());

        return TryComp<MachineFrameComponent>(target, out var frame) && frame.HasBoard &&
               HasComp<MachineBoardComponent>(frame.BoardContainer.ContainedEntities.FirstOrNull());
    }
}
