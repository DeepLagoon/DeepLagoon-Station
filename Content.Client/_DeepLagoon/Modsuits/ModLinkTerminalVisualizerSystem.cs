using Content.Client.Clothing;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Clothing;
using Content.Shared.Item;
using Robust.Client.GameObjects;

namespace Content.Client._DeepLagoon.Modsuits;

[RegisterComponent]
public sealed partial class ModLinkTerminalVisualsComponent : Component;

public sealed partial class ModLinkTerminalVisualizerSystem : VisualizerSystem<ModLinkTerminalVisualsComponent>
{
    [Dependency] private SharedItemSystem _items = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ModLinkTerminalVisualsComponent, GetEquipmentVisualsEvent>(OnGetEquipmentVisuals,
            after: [typeof(ClientClothingSystem)]);
    }

    protected override void OnAppearanceChange(EntityUid uid, ModLinkTerminalVisualsComponent component, ref AppearanceChangeEvent args)
    {
        _items.VisualsChanged(uid);
    }

    private void OnGetEquipmentVisuals(Entity<ModLinkTerminalVisualsComponent> terminal, ref GetEquipmentVisualsEvent args)
    {
        if (args.Slot is not ("neck" or "neck2" or "neck3") ||
            !AppearanceSystem.TryGetData(terminal, ModLinkVisuals.Connected, out bool connected) || !connected)
            return;

        args.Layers.Add(($"{args.Slot}-modlink-connected", new PrototypeLayerData
        {
            RsiPath = "_DeepLagoon/Modsuits/modlink_worn.rsi",
            State = "equipped-NECK-active",
            Shader = "unshaded",
        }));
    }
}
