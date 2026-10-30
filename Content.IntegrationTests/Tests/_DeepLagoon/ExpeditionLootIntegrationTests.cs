using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Salvage;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Salvage.Expeditions;
using Content.Server.VendingMachines;
using Content.Shared.VendingMachines;
using Content.Shared.Climbing.Components;
using Content.Shared.Gravity;
using Content.Shared.Item;
using Content.Shared.Movement.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.NPC;
using Content.Shared.Mobs.Components;
using Content.Shared.Salvage;
using Content.Shared.Storage.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExpeditionLootIntegrationTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: vendingMachineInventory
  id: DLExpeditionUnsafeVending
  startingInventory:
    WeaponRifleMR8C: 1
    WeaponCaseLongBulldog: 1
- type: entity
  id: DLExpeditionVendingProbe
  parent: VendingMachineYouTool
  components:
  - type: VendingMachine
    pack: DLExpeditionUnsafeVending
";

    private static Entity<MapGridComponent> Expedition(IEntityManager entities, DifficultyRating difficulty)
    {
        var map = entities.System<SharedMapSystem>().CreateMap(out _);
        var grid = entities.EnsureComponent<MapGridComponent>(map);
        var expedition = entities.AddComponent<SalvageExpeditionComponent>(map);
        expedition.Difficulty = difficulty;
        expedition.EndTime = TimeSpan.FromDays(1);
        entities.System<ExpeditionLootSystem>().InitializeBudget(map, difficulty);
        return (map, grid);
    }

    [Test]
    public async Task EveryClassifiedItemAndNestedCaseRespectsTheCap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var loot = entities.System<ExpeditionLootSystem>();
            var profile = pair.Server.ResolveDependency<IPrototypeManager>().Index<ExpeditionLootPrototype>("DeepLagoonExpeditionLoot");
            Assert.That(loot.ResolvePrototype(new EntProtoId("SpawnDungeonLootGunT1"), 3), Is.EqualTo(new EntProtoId("SpawnDungeonLootGunT3")));
            Assert.That(loot.ResolvePrototype(new EntProtoId("SpawnDungeonLootGunT1"), 2), Is.EqualTo(new EntProtoId("SpawnDungeonLootGunT2")));
            Assert.That(loot.ResolvePrototype(new EntProtoId("SpawnDungeonLootMeleeT1"), 3), Is.EqualTo(new EntProtoId("SpawnDungeonLootMeleeT3")));
            Assert.That(loot.ResolvePrototype(new EntProtoId("SpawnDungeonLootExplosivesT1"), 3), Is.EqualTo(new EntProtoId("SpawnDungeonLootExplosivesT3")));
            foreach (var cap in new[] { 1, 2, 3 })
            foreach (var id in profile.Tiers.Values.SelectMany(x => x))
                Assert.That(loot.GetTier(loot.ResolvePrototype(id, cap)), Is.LessThanOrEqualTo(cap), $"{id}, cap {cap}");
            foreach (var id in profile.Tier4Rewards)
                Assert.That(loot.GetTier(id), Is.EqualTo(4), id.Id);
            foreach (var id in profile.Tier5Rewards)
                Assert.That(loot.GetTier(id), Is.EqualTo(5), id.Id);
            foreach (var difficulty in Enum.GetValues<DifficultyRating>())
            {
                var grid = Expedition(entities, difficulty);
                var coordinates = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
                foreach (var id in new[] { "WeaponCaseMr8c", "WeaponCaseHeavySolarisExpedition", "SpawnDungeonLootGunT5", "SpawnDungeonLootGunT3", "SpawnDungeonLootRnDDisk", "SpawnDungeonLootCrateArmoryWeapon" })
                    loot.SpawnLoot(grid, id, coordinates);
                foreach (var (origin, metadata) in entities.EntityQuery<ExpeditionLootOriginComponent, MetaDataComponent>().ToArray())
                {
                    if (origin.Expedition != grid.Owner || metadata.EntityPrototype == null)
                        continue;
                    Assert.That(loot.GetTier(metadata.EntityPrototype.ID), Is.LessThanOrEqualTo(ExpeditionLootSystem.MaximumTier(difficulty)), metadata.EntityPrototype.ID);
                }
                entities.DeleteEntity(grid);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LootTakenHomeAndOpenedAfterMapDeletionCannotRecoverTheOriginalHighTierGun()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var loot = entities.System<ExpeditionLootSystem>();
            var map = Expedition(entities, DifficultyRating.Minimal);
            var box = loot.SpawnLoot(map, "ClothingBackpackSyndicateCommanderLootA", new EntityCoordinates(map, Vector2.Zero));
            var home = entities.System<SharedMapSystem>().CreateMap(out _);
            var homeCoordinates = new EntityCoordinates(home, Vector2.Zero);
            entities.System<SharedTransformSystem>().SetCoordinates(box, homeCoordinates);
            entities.DeleteEntity(map);
            var user = entities.SpawnEntity("MobHuman", homeCoordinates);
            entities.EventBus.RaiseLocalEvent(box, new UseInHandEvent(user));
            var guns = entities.EntityQuery<GunComponent, MetaDataComponent>().Where(x => x.Item2.EntityPrototype?.ID == "WeaponRifleMR8C").ToArray();
            Assert.That(guns, Is.Empty, "Unopened loot must retain its origin after the expedition is deleted.");
            Assert.That(entities.EntityQuery<MetaDataComponent>().Any(m => m.EntityPrototype?.ID == "WeaponPistolMk58Expedition"), Is.True);
            var ordinary = entities.SpawnEntity("ClothingBackpackSyndicateCommanderLootA", homeCoordinates);
            entities.EventBus.RaiseLocalEvent(ordinary, new UseInHandEvent(user));
            Assert.That(entities.EntityQuery<GunComponent, MetaDataComponent>().Any(x => x.Item2.EntityPrototype?.ID == "WeaponRifleMR8C"), Is.True,
                "Ordinary non-expedition loot must keep its original contents.");
            entities.DeleteEntity(home);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NpcDeathAndCompletionShareOneRareEntitlement()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var loot = entities.System<ExpeditionLootSystem>();
            var map = Expedition(entities, DifficultyRating.Extreme);
            var coords = new EntityCoordinates(map, Vector2.Zero);
            var npc = entities.SpawnEntity("MobSyndicateNavalCommanderA", coords);
            Assert.That(entities.GetComponent<HTNComponent>(npc).Blackboard.GetValue<bool>(NPCBlackboard.NavClimb), Is.True);
            Assert.That(entities.GetComponent<ClimbingComponent>(npc).CanClimb, Is.True);
            var drop = entities.GetComponent<ExpeditionRareDropComponent>(npc);
            Assert.That(drop.Candidates, Does.Contain(new EntProtoId("WeaponRifleMR8C")));
            var budget = entities.GetComponent<ExpeditionLootBudgetComponent>(map);
            budget.RareTier = 5;
            loot.InitializeBudget(map, DifficultyRating.Extreme);
            Assert.That(budget.RareTier, Is.EqualTo(5), "Initializing twice must not reroll.");
            var state = entities.GetComponent<MobStateComponent>(npc);
            var ev = new MobStateChangedEvent(npc, state, MobState.Alive, MobState.Dead);
            entities.EventBus.RaiseLocalEvent(npc, ev);
            Assert.That(drop.Processed, Is.True);
            Assert.That(budget.Claimed, Is.True, "NPC death must consume the entitlement before the completion fallback.");
            entities.EventBus.RaiseLocalEvent(npc, ev);
            entities.GetComponent<SalvageExpeditionComponent>(map).Completed = true;
            loot.GiveCompletionReward(map, coords);
            loot.GiveCompletionReward(map, coords);
            Assert.That(budget.Claimed, Is.True);
            var awarded = entities.EntityQuery<GunComponent, ItemComponent, MetaDataComponent>(true)
                .Where(x => x.Item3.EntityPrototype is { } proto && loot.GetTier(proto.ID) >= 4 &&
                    entities.TryGetComponent<ExpeditionLootOriginComponent>(x.Item1.Owner, out var origin) && origin.Expedition == map.Owner)
                .Select(x => x.Item3.EntityPrototype!.ID).ToArray();
            Assert.That(awarded, Has.Length.EqualTo(1), $"Candidates: {string.Join(",", drop.Candidates)}; awards: {string.Join(",", awarded)}");
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VisitingShuttleEquipmentIsNotDowngraded()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var expedition = Expedition(entities, DifficultyRating.Minimal);
            var ship = entities.System<SharedMapSystem>().CreateGridEntity(entities.GetComponent<MapComponent>(expedition).MapId);
            var gun = entities.SpawnEntity("WeaponRifleMR8C", new EntityCoordinates(ship, Vector2.Zero));
            Assert.That(entities.HasComponent<ExpeditionLootOriginComponent>(gun), Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(gun).EntityPrototype!.ID, Is.EqualTo("WeaponRifleMR8C"));
            entities.DeleteEntity(expedition);
        });
        await pair.CleanReturnAsync();
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExpeditionNpcActuallyCrossesACrateInANarrowCorridor(bool anchored)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        EntityUid map = default;
        EntityUid npc = default;
        EntityUid crate = default;
        var observedClimbing = false;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cfg = pair.Server.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(CCVars.NPCEnabled, true);
            cfg.SetCVar(CCVars.NPCPathfinding, true);
            cfg.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false);
            entities.System<NPCSystem>().Enabled = false; // Keep HTN planning from changing the steering target.
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out _);
            var gravity = entities.EnsureComponent<GravityComponent>(map);
            gravity.Inherent = true;
            gravity.Enabled = true;
            Entity<MapGridComponent> grid = (map, entities.EnsureComponent<MapGridComponent>(map));
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = -1; x <= 7; x++)
            {
                maps.SetTile(grid, new Vector2i(x, 0), floor);
                for (var y = -1; y <= 1; y += 2)
                {
                    maps.SetTile(grid, new Vector2i(x, y), floor);
                    entities.SpawnEntity("WallSolid", new EntityCoordinates(map, new Vector2(x + 0.5f, y + 0.5f)));
                }
            }
            crate = entities.SpawnEntity("CrateGenericSteel", new EntityCoordinates(map, new Vector2(2.5f, 0.5f)));
            Assert.That(entities.HasComponent<ClimbableComponent>(crate), Is.True);
            if (anchored)
                entities.System<SharedTransformSystem>().AnchorEntity(crate);
            npc = entities.CreateEntityUninitialized("MobMercenarySoldierKnife", new EntityCoordinates(map, new Vector2(0.5f, 0.5f)));
            var origin = entities.AddComponent<ExpeditionLootOriginComponent>(npc);
            origin.Expedition = map;
            origin.MaximumTier = 3;
            origin.Difficulty = DifficultyRating.Extreme;
            entities.InitializeAndStartEntity(npc);
            entities.GetComponent<HTNComponent>(npc).Enabled = false;
            entities.EnsureComponent<ActiveNPCComponent>(npc);
        });
        // Bake the new navigation grid before requesting a path.
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<NPCSteeringSystem>().Register(npc,
            new EntityCoordinates(map, new Vector2(5.5f, 0.5f))));
        for (var step = 0; step < 96; step++)
        {
            await pair.RunTicksSync(5);
            await pair.Server.WaitAssertion(() =>
                observedClimbing |= pair.Server.EntMan.GetComponent<ClimbingComponent>(npc).IsClimbing);
        }
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<TransformComponent>(npc).LocalPosition.X, Is.GreaterThan(3.3f),
                $"NPC must pass the crate: active={entities.HasComponent<ActiveNPCComponent>(npc)}, " +
                $"canMove={entities.GetComponent<InputMoverComponent>(npc).CanMove}, " +
                $"steering={entities.GetComponent<NPCSteeringComponent>(npc).Status}, " +
                $"flags={entities.GetComponent<NPCSteeringComponent>(npc).Flags}, climbed={observedClimbing}");
            Assert.That(observedClimbing, Is.True, "Crossing must use native climbing, rather than push the crate.");
            Assert.That(entities.EntityExists(crate), Is.True, "The route must be crossed by vaulting, not by destroying the crate.");
            entities.DeleteEntity(map);
            entities.System<NPCSystem>().Enabled = true;
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, true);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VendingStockAndRestocksKeepTheExpeditionCapAfterTransport()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var loot = entities.System<ExpeditionLootSystem>();
            var expedition = Expedition(entities, DifficultyRating.Minimal);
            var machine = loot.SpawnLoot(expedition, "DLExpeditionVendingProbe", new EntityCoordinates(expedition, Vector2.Zero));
            var stock = entities.GetComponent<VendingMachineComponent>(machine);
            Assert.That(stock.Inventory, Is.Not.Empty);
            Assert.That(stock.Inventory.Values.All(x => loot.GetTier(x.ID) <= 1), Is.True);
            var home = entities.System<SharedMapSystem>().CreateMap(out _);
            entities.System<SharedTransformSystem>().SetCoordinates(machine, new EntityCoordinates(home, Vector2.Zero));
            entities.DeleteEntity(expedition);
            entities.System<VendingMachineSystem>().TryRestockInventory(machine, stock);
            Assert.That(stock.Inventory.Values.All(x => loot.GetTier(x.ID) <= 1), Is.True);
            var ordinary = entities.SpawnEntity("DLExpeditionVendingProbe", new EntityCoordinates(home, Vector2.Zero));
            Assert.That(entities.GetComponent<VendingMachineComponent>(ordinary).Inventory.ContainsKey("WeaponRifleMR8C"), Is.True);
            entities.DeleteEntity(home);
        });
        await pair.CleanReturnAsync();
    }

}
