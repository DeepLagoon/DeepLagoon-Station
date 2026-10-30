using System.Linq;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.Salvage.Expeditions;
using Content.Server.Spawners.Components;
using Content.Server.Storage.Components;
using Content.Shared.Climbing.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Roles;
using Content.Shared.Salvage;
using Content.Shared.Station;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.VendingMachines;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Salvage;

/// <summary>
/// Applies the same progression to room markers, nested crates, equipment and delayed NPC loot.
/// The visitor's existing equipment is never scanned or confiscated.
/// </summary>
public sealed class ExpeditionLootSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedStationSpawningSystem _station = default!;

    private static readonly string[] WeaponMarkerPrefixes =
    {
        "SpawnDungeonLootGunT", "SpawnDungeonLootMeleeT", "SpawnDungeonLootExplosivesT",
    };
    private ExpeditionLootOriginComponent? _spawnOrigin;

    private ExpeditionLootPrototype Profile => _prototypes.Index<ExpeditionLootPrototype>("DeepLagoonExpeditionLoot");

    public override void Initialize()
    {
        base.Initialize();
        EntityManager.EntityInitialized += OnInitialized;
        SubscribeLocalEvent<ExpeditionLootOriginComponent, MapInitEvent>(OnOriginMapInit,
            before: new[] { typeof(LoadoutSystem) }, after: new[] { typeof(SharedBodySystem) });
        SubscribeLocalEvent<ExpeditionRareDropComponent, MobStateChangedEvent>(OnMobStateChanged);
    }

    public override void Shutdown()
    {
        EntityManager.EntityInitialized -= OnInitialized;
        base.Shutdown();
    }

    public static int MaximumTier(DifficultyRating difficulty) => difficulty switch
    {
        DifficultyRating.Minimal or DifficultyRating.Minor => 1,
        DifficultyRating.Moderate => 2,
        _ => 3,
    };

    public static int RollRareTier(DifficultyRating difficulty, double roll, double tier4Chance, double tier5Chance)
    {
        if (difficulty != DifficultyRating.Extreme)
            return 0;
        if (roll < tier5Chance)
            return 5;
        return roll < tier5Chance + tier4Chance ? 4 : 0;
    }

    public int GetTier(EntProtoId id)
    {
        // Explicit entries override inheritance (e.g. Bulldog is available in the T3 table).
        foreach (var (tier, ids) in Profile.Tiers)
        {
            if (ids.Contains(id))
                return tier;
        }
        foreach (var parent in _prototypes.EnumerateParents<EntityPrototype>(id))
        {
            foreach (var (tier, ids) in Profile.Tiers)
            {
                if (ids.Contains(parent.ID))
                    return tier;
            }
        }
        // New, unclassified guns must not silently become easy-expedition loot.
        var proto = _prototypes.Index(id);
        return proto.TryGetComponent<GunComponent>(out _) && proto.TryGetComponent<Content.Shared.Item.ItemComponent>(out _) ? 5 : 1;
    }

    public bool TryGetOrigin(EntityUid source, out ExpeditionLootOriginComponent origin)
    {
        if (TryComp(source, out ExpeditionLootOriginComponent? stored))
        {
            origin = stored;
            return true;
        }
        if (_spawnOrigin != null)
        {
            origin = _spawnOrigin;
            return true;
        }
        var xform = Transform(source);
        if (xform.MapUid is { } map && xform.GridUid == map &&
            TryComp(map, out SalvageExpeditionComponent? expedition))
        {
            origin = new ExpeditionLootOriginComponent
            {
                Difficulty = expedition.Difficulty,
                MaximumTier = MaximumTier(expedition.Difficulty),
                Expedition = map,
            };
            return true;
        }
        origin = default!;
        return false;
    }

    private void OnInitialized(Entity<MetaDataComponent> entity)
    {
        if (!TryGetOrigin(entity, out var origin))
            return;
        BindOrigin(entity, origin);
        if (TryComp(entity, out HTNComponent? npc))
        {
            npc.Blackboard.SetValue(NPCBlackboard.NavClimb, true);
            EnsureComp<ClimbingComponent>(entity).CanClimb = true;
        }
    }

    private void BindOrigin(EntityUid uid, ExpeditionLootOriginComponent origin)
    {
        var component = EnsureComp<ExpeditionLootOriginComponent>(uid);
        component.Difficulty = origin.Difficulty;
        component.MaximumTier = origin.MaximumTier;
        component.Expedition = origin.Expedition;
    }

    public EntProtoId ResolvePrototype(EntityUid source, EntProtoId id)
    {
        if (!TryGetOrigin(source, out var origin))
            return id;
        return ResolvePrototype(id, origin.MaximumTier);
    }

    public EntProtoId ResolvePrototype(EntProtoId id, int maximumTier)
    {
        var name = id.Id;
        // A hard raid should normally produce T3 weapons, not merely permit rare upgrades from T1.
        var ordinaryTier = Math.Min(3, maximumTier);
        foreach (var prefix in WeaponMarkerPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(prefix.Length), out var tier) && tier < ordinaryTier)
            {
                id = new EntProtoId($"{prefix}{ordinaryTier}");
                name = id.Id;
                break;
            }
        }
        if (name.StartsWith("SpaceCash", StringComparison.Ordinal) && int.TryParse(name.AsSpan(9), out var cash))
        {
            var maximum = maximumTier switch { 1 => 100, 2 => 500, _ => 2500 };
            if (cash > maximum)
                return new EntProtoId($"SpaceCash{maximum}");
        }
        var visited = new HashSet<EntProtoId>();
        while (GetTier(id) > maximumTier && visited.Add(id))
        {
            if (Profile.Replacements.TryGetValue(id, out var replacement))
            {
                id = replacement;
                continue;
            }
            // A descendant of a classified weapon uses the same fallback as its ancestor.
            var inherited = _prototypes.EnumerateParents<EntityPrototype>(id)
                .FirstOrDefault(parent => Profile.Replacements.ContainsKey(parent.ID));
            var proto = _prototypes.Index(id);
            id = inherited != null ? Profile.Replacements[inherited.ID]
                : proto.TryGetComponent<GunComponent>(out _) && proto.TryGetComponent<Content.Shared.Item.ItemComponent>(out _)
                    ? new EntProtoId("WeaponRifleLecter") : new EntProtoId("SheetSteel1");
        }
        // A bad replacement cycle must not leak restricted equipment.
        return GetTier(id) <= maximumTier ? id : new EntProtoId("SheetSteel1");
    }

    public EntityUid SpawnLoot(EntityUid source, EntProtoId? id, EntityCoordinates coordinates)
    {
        if (id == null || !TryGetOrigin(source, out var origin))
            return Spawn(id, coordinates);
        return SpawnScoped(origin, () => Spawn(ResolvePrototype(id.Value, origin.MaximumTier), coordinates));
    }

    public EntityUid SpawnLoot(EntityUid source, EntProtoId? id, MapCoordinates coordinates)
    {
        if (id == null || !TryGetOrigin(source, out var origin))
            return Spawn(id, coordinates);
        return SpawnScoped(origin, () => Spawn(ResolvePrototype(id.Value, origin.MaximumTier), coordinates));
    }

    public EntityUid SpawnLootAtPosition(EntityUid source, EntProtoId id, EntityCoordinates coordinates)
    {
        if (!TryGetOrigin(source, out var origin))
            return SpawnAtPosition(id, coordinates);
        return SpawnScoped(origin, () => SpawnAtPosition(ResolvePrototype(id, origin.MaximumTier), coordinates));
    }

    public EntityUid SpawnLootNextToOrDrop(EntityUid source, EntProtoId id)
    {
        if (!TryGetOrigin(source, out var origin))
            return EntityManager.SpawnNextToOrDrop(id, source);
        return SpawnScoped(origin, () => EntityManager.SpawnNextToOrDrop(ResolvePrototype(id, origin.MaximumTier), source));
    }

    private EntityUid SpawnScoped(ExpeditionLootOriginComponent origin, Func<EntityUid> spawn)
    {
        // EntityInitialized binds provenance before MapInit, even for nullspace/container spawns.
        // Native spawning retains its rotation, container and position semantics.
        var previous = _spawnOrigin;
        _spawnOrigin = origin;
        try
        {
            return spawn();
        }
        finally
        {
            _spawnOrigin = previous;
        }
    }

    public void FilterVendingInventory(EntityUid uid, VendingMachineComponent vending)
    {
        if (!TryGetOrigin(uid, out var origin))
            return;
        foreach (var inventory in new[] { vending.Inventory, vending.ContrabandInventory, vending.EmaggedInventory })
        {
            var entries = inventory.Values.ToArray();
            inventory.Clear();
            foreach (var entry in entries)
            {
                var replacement = ResolvePrototype(entry.ID, origin.MaximumTier).Id;
                if (inventory.TryGetValue(replacement, out var existing))
                    existing.Amount = (uint) Math.Min(uint.MaxValue, (ulong) existing.Amount + entry.Amount);
                else
                    inventory[replacement] = new VendingMachineInventoryEntry(entry.Type, replacement, entry.Amount);
            }
        }
        Dirty(uid, vending);
    }

    public void InitializeBudget(EntityUid map, DifficultyRating difficulty)
    {
        if (HasComp<ExpeditionLootBudgetComponent>(map))
            return;
        var budget = AddComp<ExpeditionLootBudgetComponent>(map);
        budget.RareTier = RollRareTier(difficulty, _random.NextDouble(), Profile.Tier4Chance, Profile.Tier5Chance);
    }

    private void OnOriginMapInit(EntityUid uid, ExpeditionLootOriginComponent origin, MapInitEvent args)
    {
        if (TryComp(uid, out LoadoutComponent? loadout))
            OnLoadout(uid, loadout, args);
    }

    private void OnLoadout(EntityUid uid, LoadoutComponent component, MapInitEvent args)
    {
        if (!HasComp<HTNComponent>(uid) || !TryGetOrigin(uid, out _) || component.StartingGear is not { Count: > 0 } gear)
            return;
        var original = _prototypes.Index(_random.Pick(gear));
        var contents = original.Equipment.Values.Concat(original.Inhand).Concat(original.Storage.Values.SelectMany(x => x));
        var candidates = new HashSet<EntProtoId>();
        foreach (var id in contents)
            FindRareWeapons(id, candidates, new HashSet<EntProtoId>());
        if (candidates.Count > 0)
            EnsureComp<ExpeditionRareDropComponent>(uid).Candidates = candidates.ToList();

        var replacement = new StartingGearPrototype
        {
            Equipment = original.Equipment.ToDictionary(x => x.Key, x => ResolvePrototype(uid, x.Value)),
            Inhand = original.Inhand.Select(x => ResolvePrototype(uid, x)).ToList(),
            Storage = original.Storage.ToDictionary(x => x.Key, x => x.Value.Select(id => ResolvePrototype(uid, id)).ToList()),
        };
        _station.EquipStartingGear(uid, replacement, raiseEvent: false);
        // LoadoutSystem still processes role loadouts and raises the normal equipped event.
        component.StartingGear = null;
    }

    private void FindRareWeapons(EntProtoId id, HashSet<EntProtoId> result, HashSet<EntProtoId> visited)
    {
        if (!visited.Add(id) || visited.Count > 512)
            return;
        var proto = _prototypes.Index(id);
        if (GetTier(id) >= 4 && proto.TryGetComponent<GunComponent>(out _) && proto.TryGetComponent<Content.Shared.Item.ItemComponent>(out _))
            result.Add(id);
        if (proto.TryGetComponent<SpawnItemsOnUseComponent>(out var use))
        {
            foreach (var entry in use.Items)
                if (entry.PrototypeId is { } child)
                    FindRareWeapons(child, result, visited);
        }
        if (proto.TryGetComponent<StorageFillComponent>(out var fill))
        {
            foreach (var entry in fill.Contents)
                if (entry.PrototypeId is { } child)
                    FindRareWeapons(child, result, visited);
        }
        if (proto.TryGetComponent<RandomSpawnerComponent>(out var spawner))
        {
            foreach (var child in spawner.Prototypes.Concat(spawner.RarePrototypes))
                FindRareWeapons(child, result, visited);
        }
    }

    private void OnMobStateChanged(EntityUid uid, ExpeditionRareDropComponent component, MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead || component.Processed || !TryGetOrigin(uid, out var origin))
            return;
        component.Processed = true;
        if (!TryComp(origin.Expedition, out ExpeditionLootBudgetComponent? budget) || budget.Claimed || budget.RareTier == 0)
            return;
        var candidates = component.Candidates.Where(id => GetTier(id) == budget.RareTier).ToList();
        if (candidates.Count == 0)
            return;
        budget.Claimed = true;
        SpawnRare(origin, _random.Pick(candidates), Transform(uid).Coordinates, budget.RareTier);
    }

    public void GiveCompletionReward(EntityUid expedition, EntityCoordinates coordinates)
    {
        if (!TryComp(expedition, out ExpeditionLootBudgetComponent? budget) || budget.Claimed || budget.RareTier == 0 ||
            !TryComp(expedition, out SalvageExpeditionComponent? mission) || !mission.Completed)
            return;
        var pool = budget.RareTier == 5 ? Profile.Tier5Rewards : Profile.Tier4Rewards;
        if (pool.Count == 0)
            return;
        budget.Claimed = true;
        SpawnRare(new ExpeditionLootOriginComponent { Difficulty = mission.Difficulty, Expedition = expedition },
            _random.Pick(pool), coordinates, budget.RareTier);
    }

    private void SpawnRare(ExpeditionLootOriginComponent origin, EntProtoId prototype, EntityCoordinates coordinates, int tier)
    {
        SpawnScoped(new ExpeditionLootOriginComponent
        {
            Difficulty = origin.Difficulty, Expedition = origin.Expedition, MaximumTier = tier,
        }, () => Spawn(prototype, coordinates));
    }
}
