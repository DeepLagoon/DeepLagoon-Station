using System.Linq;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunBeaconColorComponent : Component
{
    [DataField] public string Color = "Random";
}

/// <summary>marker_beacons.dm: color, single beacon per tile and interruptible 1.5-second retrieval.</summary>
public sealed class BitrunMarkerBeaconSystem : EntitySystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedPointLightSystem _light = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private BitrunBossHazardSystem _tiles = default!;
    [Dependency] private IRobustRandom _random = default!;

    public static readonly Dictionary<string, Color> Colors = new()
    {
        ["Burgundy"] = Color.FromHex("#FA644B"), ["Bronze"] = Color.FromHex("#FA9632"),
        ["Yellow"] = Color.FromHex("#E1E17D"), ["Lime"] = Color.FromHex("#AFC84B"),
        ["Olive"] = Color.FromHex("#64C864"), ["Jade"] = Color.FromHex("#7DE1AF"),
        ["Teal"] = Color.FromHex("#40CEFF"), ["Cerulean"] = Color.FromHex("#6496FA"),
        ["Indigo"] = Color.FromHex("#6496FA"), ["Purple"] = Color.FromHex("#952CF4"),
        ["Violet"] = Color.FromHex("#9B51FF"), ["Fuchsia"] = Color.FromHex("#E17DE1"),
    };

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunBeaconColorComponent, MapInitEvent>((Entity<BitrunBeaconColorComponent> ent, ref MapInitEvent args) => SetColor(ent, ent.Comp.Color));
        SubscribeLocalEvent<BitrunBeaconColorComponent, GetVerbsEvent<AlternativeVerb>>(OnColorVerbs);
        SubscribeLocalEvent<BitrunBeaconColorComponent, UseInHandEvent>(OnPlace, before: [typeof(BitrunUtilityItemsSystem)]);
        SubscribeLocalEvent<BitrunPlacedBeaconComponent, ActivateInWorldEvent>((Entity<BitrunPlacedBeaconComponent> ent, ref ActivateInWorldEvent args) =>
        {
            if (args.Handled) return;
            args.Handled = true;
            Retrieve(ent, args.User);
        });
        SubscribeLocalEvent<BitrunPlacedBeaconComponent, InteractUsingEvent>((Entity<BitrunPlacedBeaconComponent> ent, ref InteractUsingEvent args) =>
        {
            if (args.Handled || !HasComp<BitrunBeaconColorComponent>(args.Used) || !HasComp<StackComponent>(args.Used)) return;
            args.Handled = true;
            Retrieve(ent, args.User, args.Used);
        });
        SubscribeLocalEvent<BitrunPlacedBeaconComponent, BitrunBeaconRetrieveEvent>(OnRetrieved);
    }

    public void SetColor(Entity<BitrunBeaconColorComponent> beacon, string color)
    {
        var placed = HasComp<BitrunPlacedBeaconComponent>(beacon);
        if (!Colors.ContainsKey(color)) color = placed ? _random.Pick(Colors.Keys.ToArray()) : "Random";
        beacon.Comp.Color = color;
        _appearance.SetData(beacon, BitrunEquipmentVisuals.Mode, color.ToLowerInvariant());
        if (placed) _light.SetColor(beacon, Colors[color]);
    }

    private void OnColorVerbs(Entity<BitrunBeaconColorComponent> beacon, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        foreach (var color in Colors.Keys.Prepend("Random"))
        {
            var selected = color;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("bitrun-beacon-color-" + color.ToLowerInvariant()),
                Category = new VerbCategory("bitrun-beacon-color", null),
                Act = () => { if (!Deleted(beacon)) SetColor(beacon, selected); },
            });
        }
    }

    private void OnPlace(Entity<BitrunBeaconColorComponent> item, ref UseInHandEvent args)
    {
        if (args.Handled || !HasComp<StackComponent>(item)) return;
        args.Handled = true;
        var position = _tiles.Center(Transform(args.User).Coordinates);
        if (Transform(args.User).GridUid == null || _containers.IsEntityInContainer(args.User) ||
            _lookup.GetEntitiesInRange<BitrunPlacedBeaconComponent>(position, 0.75f).Any(ent => _tiles.SameTile(position, Transform(ent).Coordinates)) ||
            !_stacks.Use(item, 1)) return;
        var placed = Spawn("BitrunPlacedMarkerBeacon", position);
        SetColor((placed, Comp<BitrunBeaconColorComponent>(placed)), item.Comp.Color);
        _transform.AnchorEntity((placed, Transform(placed), MetaData(placed)), position);
    }

    public bool Retrieve(EntityUid beacon, EntityUid user, EntityUid? stack = null)
    {
        if (stack is {} uid && (!TryComp<StackComponent>(uid, out var count) || _stacks.GetAvailableSpace(count) < 1)) return false;
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, 1.5f, new BitrunBeaconRetrieveEvent(), beacon, target: beacon, used: stack)
        {
            BreakOnMove = true, BreakOnDamage = true, NeedHand = true,
        });
    }

    private void OnRetrieved(Entity<BitrunPlacedBeaconComponent> beacon, ref BitrunBeaconRetrieveEvent args)
    {
        if (args.Handled || args.Cancelled || TerminatingOrDeleted(beacon)) return;
        args.Handled = true;
        if (args.Used is {} stack)
        {
            if (!TryComp<StackComponent>(stack, out var count) || _stacks.GetAvailableSpace(count) < 1) return;
            _stacks.SetCount(stack, count.Count + 1, count);
        }
        else
        {
            var item = Spawn("BitrunSourceObjItemStackMarkerBeacon", Transform(beacon).Coordinates);
            SetColor((item, Comp<BitrunBeaconColorComponent>(item)), Comp<BitrunBeaconColorComponent>(beacon).Color);
            _stacks.TryMergeToHands(item, args.User);
        }
        QueueDel(beacon);
    }
}
