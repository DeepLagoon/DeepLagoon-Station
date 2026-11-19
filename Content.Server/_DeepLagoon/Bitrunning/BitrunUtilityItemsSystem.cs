using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Stacks;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunDeployableComponent : Component
{
    [DataField(required: true)] public EntProtoId Prototype;
    [DataField] public bool Mount;
}

[RegisterComponent]
public sealed partial class BitrunHoverboardComponent : Component;

[RegisterComponent]
public sealed partial class BitrunPlacedBeaconComponent : Component;

[RegisterComponent]
public sealed partial class BitrunDiscoBallComponent : Component
{
    public TimeSpan NextColor;
}

[RegisterComponent]
public sealed partial class BitrunEvidenceBagComponent : Component;

/// <summary>Source utility callbacks, shared by map instances and equipment spawned directly.</summary>
public sealed class BitrunUtilityItemsSystem : EntitySystem
{
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedPointLightSystem _light = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedItemSystem _items = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedBuckleSystem _buckle = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunDeployableComponent, UseInHandEvent>(OnDeploy);
        SubscribeLocalEvent<BitrunEvidenceBagComponent, ItemSlotInsertAttemptEvent>(OnBagInsert);
        SubscribeLocalEvent<BitrunEvidenceBagComponent, EntInsertedIntoContainerMessage>(OnBagChanged);
        SubscribeLocalEvent<BitrunEvidenceBagComponent, EntRemovedFromContainerMessage>(OnBagRemoved);
        SubscribeLocalEvent<BitrunHoverboardComponent, GetVerbsEvent<InteractionVerb>>(OnBoardVerbs);
    }

    private void OnBagInsert(Entity<BitrunEvidenceBagComponent> bag, ref ItemSlotInsertAttemptEvent args)
    {
        if (!TryComp<ItemComponent>(args.Item, out var item) ||
            _prototypes.Index(item.Size) > _prototypes.Index<ItemSizePrototype>("Normal"))
            args.Cancelled = true;
    }

    private void OnBagChanged(Entity<BitrunEvidenceBagComponent> bag, ref EntInsertedIntoContainerMessage args)
    {
        if (TryComp<ItemComponent>(args.Entity, out var item))
            _items.SetSize(bag, item.Size);
    }

    private void OnBagRemoved(Entity<BitrunEvidenceBagComponent> bag, ref EntRemovedFromContainerMessage args)
    {
        _items.SetSize(bag, "Tiny");
    }

    private void OnDeploy(Entity<BitrunDeployableComponent> item, ref UseInHandEvent args)
    {
        if (args.Handled || Transform(args.User).GridUid == null)
            return;
        args.Handled = true;
        var coordinates = Transform(args.User).Coordinates;
        // A stack places one beacon at a time. The single-use disco remote is consumed.
        if (HasComp<StackComponent>(item) && !_stacks.Use(item, 1))
            return;
        var deployed = Spawn(item.Comp.Prototype, coordinates);
        if (item.Comp.Mount)
            _buckle.TryBuckle(args.User, args.User, deployed);
        else
            _transform.AnchorEntity((deployed, Transform(deployed), MetaData(deployed)), coordinates);
        if (!HasComp<StackComponent>(item))
            QueueDel(item);
    }

    private void OnBoardVerbs(Entity<BitrunHoverboardComponent> board, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !TryComp<StrapComponent>(board, out var strap) || strap.BuckledEntities.Count > 0)
            return;
        var user = args.User;
        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("bitrun-hoverboard-pickup"),
            Act = () =>
            {
                if (Deleted(board) || strap.BuckledEntities.Count > 0)
                    return;
                var item = Spawn("BitrunSourceObjItemMeleeSkateboardHoverboard", Transform(board).Coordinates);
                _hands.TryPickupAnyHand(user, item);
                QueueDel(board);
            },
        });
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunDiscoBallComponent, ItemToggleComponent>();
        while (query.MoveNext(out var uid, out var disco, out var toggle))
        {
            if (!toggle.Activated || disco.NextColor > _timing.CurTime)
                continue;
            disco.NextColor = _timing.CurTime + TimeSpan.FromSeconds(0.5);
            _light.SetColor(uid, new Color(_random.NextFloat(), _random.NextFloat(), _random.NextFloat()));
        }
    }
}
