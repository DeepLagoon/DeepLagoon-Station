using Content.Client.Clothing;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Clothing;
using Content.Shared.Clothing.EntitySystems;

namespace Content.Client._DeepLagoon.Modsuits;

public sealed class ModMountedHatSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<ModMountedHatComponent, GetEquipmentVisualsEvent>(OnVisuals,
            after: [typeof(ClothingSystem)]);
    }

    private void OnVisuals(Entity<ModMountedHatComponent> hat, ref GetEquipmentVisualsEvent args)
    {
        if (args.Slot != "helmetcover") return;
        var head = new GetEquipmentVisualsEvent(args.Equipee, "head");
        RaiseLocalEvent(hat, head);
        args.Layers.Clear();
        foreach (var (key, layer) in head.Layers) args.Layers.Add(("helmetcover-" + key, layer));
    }
}
