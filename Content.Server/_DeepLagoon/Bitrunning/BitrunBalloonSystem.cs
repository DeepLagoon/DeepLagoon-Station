using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Clothing.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mind;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunBalloonArtistComponent : Component;

[RegisterComponent]
public sealed partial class BitrunLongBalloonComponent : Component
{
    [DataField] public string Color = "";
    [DataField] public Dictionary<string, ChameleonClothingAppearance> ColorVisuals = new();
}

public sealed class BitrunBalloonSystem : EntitySystem
{
    private static readonly string[] Colors = ["red", "blue", "green", "yellow", "orange", "purple"];
    private static readonly string[] Animals = ["Guy", "Nukie", "Clown", "Cat", "Fly", "Podguy", "Ai", "Dog", "Xeno", "Banana", "Lizard", "Slime", "Moth", "Ethereal", "Plasmaman"];
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedItemSystem _items = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private SharedJobSystem _jobs = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunLongBalloonComponent, MapInitEvent>(OnInit);
        SubscribeLocalEvent<BitrunLongBalloonComponent, InteractUsingEvent>(OnCombine);
    }

    private void OnInit(Entity<BitrunLongBalloonComponent> balloon, ref MapInitEvent args)
    {
        if (balloon.Comp.Color.Length == 0) balloon.Comp.Color = _random.Pick(Colors);
        _appearance.SetData(balloon, BitrunEquipmentVisuals.Mode, balloon.Comp.Color);
        if (balloon.Comp.ColorVisuals.TryGetValue(balloon.Comp.Color, out var visuals)) _items.CopyVisuals(balloon, visuals);
    }

    private void OnCombine(Entity<BitrunLongBalloonComponent> balloon, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<BitrunLongBalloonComponent>(args.Used, out var other) ||
            balloon.Comp.Color == other.Color ||
            !(HasComp<BitrunBalloonArtistComponent>(args.User) ||
                _minds.TryGetMind(args.User, out var mind, out _) && _jobs.MindTryGetJob(mind, out var job) && job.ID == "Clown")) return;
        var first = Array.IndexOf(Colors, balloon.Comp.Color);
        var second = Array.IndexOf(Colors, other.Color);
        var variant = 0;
        for (var a = 0; a < Colors.Length; a++)
        for (var b = a + 1; b < Colors.Length; b++)
        {
            if (first == a && second == b || first == b && second == a)
            {
                var animal = Spawn("BitrunSourceObjItemToyBalloonAnimal" + Animals[variant], Transform(balloon).Coordinates);
                _hands.TryPickupAnyHand(args.User, animal);
                QueueDel(args.Used);
                QueueDel(balloon);
                args.Handled = true;
                return;
            }
            variant++;
        }
    }
}
