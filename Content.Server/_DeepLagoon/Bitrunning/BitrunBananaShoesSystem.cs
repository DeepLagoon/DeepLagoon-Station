using System.Numerics;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Clothing;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.Fluids.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Maps;
using Content.Shared.Popups;
using Content.Shared.Maps;
using Content.Shared.Slippery;
using Content.Shared.Stacks;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunBananaWalkerComponent : Component
{
    [DataField] public EntityUid Shoes;
    [DataField] public Vector2i? LastTile;
}

public sealed class BitrunBananaShoesServerSystem : EntitySystem
{
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private ItemToggleSystem _toggle = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunBananaShoesComponent, InteractUsingEvent>(OnLoad);
        SubscribeLocalEvent<BitrunBananaShoesComponent, UseInHandEvent>(OnRetrieve, before: [typeof(ClothingSystem)]);
        SubscribeLocalEvent<BitrunBananaShoesComponent, ClothingGotEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<BitrunBananaShoesComponent, ClothingGotUnequippedEvent>(OnUnequipped);
        SubscribeLocalEvent<BitrunBananaShoesComponent, ItemToggleActivateAttemptEvent>(OnActivate);
        SubscribeLocalEvent<BitrunBananaShoesComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<BitrunBananaWalkerComponent, MoveEvent>(OnMove);
    }

    private void OnLoad(Entity<BitrunBananaShoesComponent> shoes, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<StackComponent>(args.Used, out var material) || material.StackTypeId != "Bananium")
            return;
        args.Handled = true;
        var count = Math.Min(material.Count, (shoes.Comp.Capacity - shoes.Comp.Fuel) / shoes.Comp.BananasPerSheet);
        if (count > 0 && _stacks.Use(args.Used, count, material))
            shoes.Comp.Fuel += count * shoes.Comp.BananasPerSheet;
    }

    private void OnActivate(Entity<BitrunBananaShoesComponent> shoes, ref ItemToggleActivateAttemptEvent args)
    {
        if (shoes.Comp.Fuel > 0)
            return;
        args.Cancelled = true;
        args.Popup = Loc.GetString("bitrun-banana-shoes-empty");
    }

    private void OnRetrieve(Entity<BitrunBananaShoesComponent> shoes, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;
        args.Handled = true;
        var count = shoes.Comp.Fuel / shoes.Comp.BananasPerSheet;
        while (count > 0)
        {
            var sheet = Spawn("MaterialBananium1", Transform(shoes).Coordinates);
            var amount = Math.Min(count, _stacks.GetMaxCount(Comp<StackComponent>(sheet)));
            _stacks.SetCount(sheet, amount);
            _hands.TryPickupAnyHand(args.User, sheet);
            shoes.Comp.Fuel -= amount * shoes.Comp.BananasPerSheet;
            count -= amount;
        }
        if (shoes.Comp.Fuel == 0)
            _toggle.TryDeactivate(shoes.Owner, args.User);
    }

    private void OnEquipped(Entity<BitrunBananaShoesComponent> shoes, ref ClothingGotEquippedEvent args)
    {
        shoes.Comp.Wearer = args.Wearer;
        var walker = EnsureComp<BitrunBananaWalkerComponent>(args.Wearer);
        walker.Shoes = shoes;
        walker.LastTile = _turf.TryGetTileRef(Transform(args.Wearer).Coordinates, out var tile) ? tile.Value.GridIndices : null;
    }

    private void OnUnequipped(Entity<BitrunBananaShoesComponent> shoes, ref ClothingGotUnequippedEvent args)
    {
        shoes.Comp.Wearer = null;
        if (TryComp<BitrunBananaWalkerComponent>(args.Wearer, out var walker) && walker.Shoes == shoes.Owner)
            RemComp<BitrunBananaWalkerComponent>(args.Wearer);
    }

    private void OnMove(Entity<BitrunBananaWalkerComponent> walker, ref MoveEvent args)
    {
        if (args.OnlyRotation || !_turf.TryGetTileRef(args.NewPosition, out var tile))
            return;
        var previous = walker.Comp.LastTile;
        walker.Comp.LastTile = tile.Value.GridIndices;
        if (previous == null || previous == walker.Comp.LastTile ||
            !TryComp<BitrunBananaShoesComponent>(walker.Comp.Shoes, out var shoes) || shoes.Fuel <= 0 ||
            !_toggle.IsActivated(walker.Comp.Shoes) || args.Component.GridUid is not { } grid ||
            !TryComp<MapGridComponent>(grid, out var gridComp))
            return;
        // Teleporting across a room is not a shoe step.
        var difference = previous.Value - tile.Value.GridIndices;
        if (difference.X * difference.X + difference.Y * difference.Y > 2)
            return;
        var backwards = args.Component.LocalRotation.GetCardinalDir() switch
        {
            Direction.North => new Vector2i(0, -1), Direction.South => new Vector2i(0, 1),
            Direction.East => new Vector2i(-1, 0), _ => new Vector2i(1, 0),
        };
        Spawn("TrashBananaPeel", _maps.GridTileToLocal(grid, gridComp, tile.Value.GridIndices + backwards));
        shoes.Fuel--;
        if (shoes.Fuel == 0)
        {
            _toggle.TryDeactivate(walker.Comp.Shoes, walker.Owner);
            _popup.PopupEntity(Loc.GetString("bitrun-banana-shoes-empty"), walker, walker);
        }
    }

    private void OnExamine(Entity<BitrunBananaShoesComponent> shoes, ref ExaminedEvent args) =>
        args.PushMarkup(Loc.GetString("bitrun-banana-shoes-fuel", ("count", shoes.Comp.Fuel)));
}
