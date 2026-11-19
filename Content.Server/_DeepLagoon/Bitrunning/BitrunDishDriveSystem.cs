using System.Linq;
using System.Numerics;
using Content.Server.Construction.Components;
using Content.Server.Construction;
using Content.Server.Power.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Disposal.Components;
using Content.Shared.Disposal.Unit;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunDishDriveBoardComponent : Component
{
    [DataField] public bool Suction = true;
    [DataField] public bool Transmit = true;
}

[RegisterComponent]
public sealed partial class BitrunDishDriveComponent : Component
{
    [DataField] public float SuctionRange = 3;
    public TimeSpan NextScan;
    public TimeSpan NextTransmit;
}

[RegisterComponent]
public sealed partial class BitrunDishComponent : Component
{
    [DataField] public bool Disposable;
}

public sealed class BitrunDishDriveSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedDisposalUnitSystem _disposal = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private IGameTiming _timing = default!;

    private Container Contents(EntityUid uid) => _containers.EnsureContainer<Container>(uid, "bitrun-dishes");

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunDishDriveComponent, MapInitEvent>((Entity<BitrunDishDriveComponent> ent, ref MapInitEvent args) => Contents(ent));
        SubscribeLocalEvent<BitrunDishDriveComponent, InteractUsingEvent>(OnInsert);
        SubscribeLocalEvent<BitrunDishDriveComponent, ActivateInWorldEvent>(OnTake);
        SubscribeLocalEvent<BitrunDishDriveComponent, GetVerbsEvent<AlternativeVerb>>(OnTransmitVerb);
        SubscribeLocalEvent<BitrunDishDriveComponent, RefreshPartsEvent>((Entity<BitrunDishDriveComponent> ent, ref RefreshPartsEvent args) =>
        {
            ent.Comp.SuctionRange = 2 + args.PartRatings.GetValueOrDefault("Manipulator", 1);
            var rating = args.Parts.Sum(part => part.Part.Rating * part.Quantity());
            if (TryComp<ApcPowerReceiverComponent>(ent, out var receiver))
                receiver.Load = rating >= 9 ? 0 : MathF.Max(0, 40 - rating);
        });
        SubscribeLocalEvent<BitrunDishDriveBoardComponent, UseInHandEvent>(OnToggleSuction);
        SubscribeLocalEvent<BitrunDishDriveBoardComponent, GetVerbsEvent<AlternativeVerb>>(OnBoardVerb);
    }

    private bool IsDish(EntityUid uid, out bool trash)
    {
        if (TryComp<BitrunDishComponent>(uid, out var source))
        {
            trash = source.Disposable;
            return true;
        }
        var prototype = MetaData(uid).EntityPrototype?.ID ?? "";
        trash = prototype.StartsWith("ShardGlass", StringComparison.Ordinal) ||
                prototype.Contains("BrokenBottle", StringComparison.Ordinal) || prototype == "FoodPlateTrash" ||
                prototype.StartsWith("BitrunSourceObjItemPlateShard", StringComparison.Ordinal);
        return trash || _tags.HasTag(uid, "DrinkGlass") ||
            prototype.StartsWith("FoodPlate", StringComparison.Ordinal) || prototype.StartsWith("FoodBowl", StringComparison.Ordinal) ||
            prototype.StartsWith("BitrunSourceObjItemPlate", StringComparison.Ordinal) ||
            prototype.StartsWith("BitrunSourceObjItemKitchenFork", StringComparison.Ordinal) || prototype == "Fork";
    }

    private void OnInsert(Entity<BitrunDishDriveComponent> drive, ref InteractUsingEvent args)
    {
        if (!args.Handled && IsDish(args.Used, out _))
            args.Handled = _containers.Insert(args.Used, Contents(drive));
    }

    private void OnTake(Entity<BitrunDishDriveComponent> drive, ref ActivateInWorldEvent args)
    {
        if (args.Handled || Contents(drive).ContainedEntities.LastOrDefault() is not { Valid: true } dish) return;
        args.Handled = true;
        _containers.Remove(dish, Contents(drive));
        _hands.TryPickupAnyHand(args.User, dish);
    }

    private void OnToggleSuction(Entity<BitrunDishDriveBoardComponent> board, ref UseInHandEvent args)
    {
        if (args.Handled) return;
        args.Handled = true;
        board.Comp.Suction = !board.Comp.Suction;
    }

    private void OnBoardVerb(Entity<BitrunDishDriveBoardComponent> board, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-dish-drive-transmit-toggle"),
            Act = () => board.Comp.Transmit = !board.Comp.Transmit });
    }

    private void OnTransmitVerb(Entity<BitrunDishDriveComponent> drive, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-dish-drive-transmit"), Act = () => Transmit(drive) });
    }

    private bool Powered(EntityUid uid) => !TryComp<ApcPowerReceiverComponent>(uid, out var receiver) || receiver.Powered;

    public bool Transmit(EntityUid drive)
    {
        if (!Powered(drive)) return false;
        var bins = _lookup.GetEntitiesInRange<DisposalUnitComponent>(Transform(drive).Coordinates, 10);
        foreach (var bin in bins)
        {
            if (BitrunBossGeometry.Chebyshev(_transform.GetMapCoordinates(drive), _transform.GetMapCoordinates(bin)) > 7 ||
                !_interaction.InRangeUnobstructed(drive, bin.Owner, range: 10)) continue;
            var moved = false;
            foreach (var dish in Contents(drive).ContainedEntities.ToArray())
                if (IsDish(dish, out var trash) && trash && _disposal.TryInsert(bin, dish, null)) moved = true;
            if (moved) return true;
        }
        return false;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunDishDriveComponent, MachineComponent>();
        while (query.MoveNext(out var uid, out var drive, out var machine))
        {
            if (drive.NextScan > _timing.CurTime || !Powered(uid)) continue;
            drive.NextScan = _timing.CurTime + TimeSpan.FromSeconds(2);
            var board = machine.BoardContainer.ContainedEntities.Select(CompOrNull<BitrunDishDriveBoardComponent>).FirstOrDefault(comp => comp != null);
            if (board?.Transmit != false && drive.NextTransmit <= _timing.CurTime && Transmit(uid))
                drive.NextTransmit = _timing.CurTime + TimeSpan.FromMinutes(1);
            if (board?.Suction == false) continue;
            foreach (var item in _lookup.GetEntitiesInRange<ItemComponent>(Transform(uid).Coordinates, drive.SuctionRange * 1.42f))
            {
                if (!IsDish(item, out var trash) || _containers.IsEntityInContainer(item.Owner) ||
                    _containers.GetAllContainers(item.Owner).Any(container => container.ContainedEntities.Any(content => !HasComp<SolutionComponent>(content))) ||
                    // Glass shards have a silicon grinding solution. That is
                    // material composition, not a drink left in a dirty dish.
                    !trash && _solutions.EnumerateSolutions((item.Owner, null)).Any(solution => solution.Solution.Comp.Solution.Volume > 0) ||
                    BitrunBossGeometry.Chebyshev(_transform.GetMapCoordinates(uid), _transform.GetMapCoordinates(item)) > drive.SuctionRange ||
                    !_interaction.InRangeUnobstructed(uid, item.Owner, range: drive.SuctionRange * 1.42f)) continue;
                var delta = _transform.GetWorldPosition(uid) - _transform.GetWorldPosition(item);
                if (delta.LengthSquared() <= 2.25f)
                    _containers.Insert(item.Owner, Contents(uid));
                else
                    _transform.SetWorldPosition(item.Owner, _transform.GetWorldPosition(item) + new Vector2(Math.Sign(delta.X), Math.Sign(delta.Y)));
            }
        }
    }
}
