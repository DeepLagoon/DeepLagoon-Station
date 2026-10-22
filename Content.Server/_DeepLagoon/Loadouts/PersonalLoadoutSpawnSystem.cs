using Content.Server.Traits;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;

namespace Content.Server._DeepLagoon.Loadouts;

public sealed partial class PersonalLoadoutSpawnSystem : EntitySystem
{
    [Dependency] private PersonalLoadoutEquipSystem _equipment = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawned, after: new[] { typeof(TraitSystem) });
        SubscribeLocalEvent<PersonalLoadoutVisualsComponent, ExaminedEvent>(OnExamined);
    }

    private void OnSpawned(PlayerSpawnCompleteEvent args)
    {
        if (args.JobId == null)
            return;
        var failed = _equipment.Apply(args.Mob, args.Profile, args.JobId, args.Player);
        foreach (var item in failed)
        {
            if (_inventory.TryGetSlotEntity(args.Mob, "back", out var backpack) && HasComp<StorageComponent>(backpack) && _storage.Insert(backpack.Value, item, out _, playSound: false))
                continue;
            // Leave anything that does not fit on the floor rather than silently deleting it.
            _hands.TryPickupAnyHand(args.Mob, item, checkActionBlocker: false);
        }
    }

    private void OnExamined(EntityUid uid, PersonalLoadoutVisualsComponent component, ExaminedEvent args)
    {
        if (_appearance.TryGetData<bool>(uid, PersonalLoadoutVisuals.Heirloom, out var heirloom) && heirloom)
            args.PushMarkup(Loc.GetString("dl-loadout-heirloom-examine"));
    }
}
