using Content.Server.Station.Systems;
using Content.Server.Traits;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Preferences;
using Content.Shared.Station;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;

namespace Content.Server._DeepLagoon.Loadouts;

public sealed partial class PersonalLoadoutSpawnSystem : EntitySystem
{
    [Dependency] private PersonalLoadoutEquipSystem _equipment = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private StationSpawningSystem _spawning = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawned, after: new[] { typeof(TraitSystem) });
        SubscribeLocalEvent<PersonalLoadoutVisualsComponent, ExaminedEvent>(OnExamined);
    }

    private void OnSpawned(PlayerSpawnCompleteEvent args)
    {
        TryComp<DeferredStartingGearComponent>(args.Mob, out var deferred);
        var failed = args.JobId is { } job
            ? _equipment.Apply(args.Mob, args.Profile, job, args.Player, deferredStorage: deferred?.Items,
                handsApplied: deferred?.HandsApplied == true, startingContainers: deferred?.Contents)
            : null;
        _spawning.FinishDeferredInhandGear(args.Mob);
        _spawning.FinishDeferredStartingGear(args.Mob);
        if (failed == null)
            return;
        var backpack = _spawning.GetBackpackStorage(args.Mob);
        foreach (var item in failed)
        {
            if (backpack is { } bag && _storage.Insert(bag, item, out _, storageComp: bag.Comp, playSound: false))
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

[RegisterComponent]
public sealed partial class DeferredStartingHandGearComponent : Component
{
    public List<StartingGearHandItem> Items = new();
    public IReadOnlyDictionary<string, string>? Assignments;
}

[RegisterComponent]
public sealed partial class DeferredStartingGearComponent : Component
{
    public List<StartingGearStorageItem> Items = new();
    public IReadOnlyDictionary<string, BackpackItemPlacement>? Layout;
    public StartingContainerContents? Contents;
    public HumanoidCharacterProfile? Profile;
    public bool HandsApplied;
}
