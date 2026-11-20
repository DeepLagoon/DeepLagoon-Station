using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Performance;
using Content.Server._Mono.GridClaimer;
using Content.Server._NF.Bank;
using Content.Server._NF.SectorServices;
using Content.Server._NF.StationEvents.Components;
using Content.Server.Cargo.Systems;
using Content.Server.GameTicking;
using Content.Server.StationEvents.Components;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using RetirementClock = System.Diagnostics.Stopwatch;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BluespaceGridRetirementTests
{
    private const string Rule = "BluespaceDungeonRetirementTest";

    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: BluespaceDungeonRetirementTest
          components:
          - type: GameRule
          - type: StationEvent
            weight: 0
          - type: BluespaceErrorRule
            groups: {}
            extendIfPopulated: false
            rewardAccounts:
              Frontier: 0.25

        - type: entity
          id: DLTestRetirementValuable
          components:
          - type: Item
          - type: StaticPrice
            price: 10000
        """;

    [Test]
    public Task NativeEndRetiresLinkedFamilyPreservingPlayersContainersClaimsAndOneReward() => RunRetirement(0);

    [Test]
    public Task NativeFourThousandItemRetirementMicrobenchmark() => RunRetirement(4096);

    private static async Task RunRetirement(int stock)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var publicMap = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        var staged = em.System<StagedMapDeletionSystem>();
        var config = pair.Server.ResolveDependency<IConfigurationManager>();
        var previousLevel = config.GetCVar(Robust.Shared.CVars.PerformanceLogLevel);
        EntityUid root = default, fragment = default, claimed = default, privateMap = default;
        EntityUid player = default, fragmentPlayer = default, backpack = default, carriedCargo = default;
        var discarded = new List<EntityUid>();
        int mapCountBefore = 0, expectedReward = 0, rewardLedgerAfter = 0;
        SectorBankComponent sector = default!;
        double endMs = 0, totalSlicesMs = 0, maximumSliceMs = 0;
        var slices = 0;
        TimeSpan endGcPause = default;

        int LiveMaps()
        {
            var count = 0;
            var query = em.AllEntityQueryEnumerator<MapComponent, MetaDataComponent>();
            while (query.MoveNext(out _, out _, out var meta))
                if (meta.EntityLifeStage < EntityLifeStage.Terminating)
                    count++;
            return count;
        }

        try
        {
            await pair.Server.WaitAssertion(() =>
            {
                config.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, 2);
                var maps = em.System<SharedMapSystem>();
                var transform = em.System<SharedTransformSystem>();
                var physics = em.System<SharedPhysicsSystem>();
                var containers = em.System<SharedContainerSystem>();
                var bank = em.System<BankSystem>();
                var ticker = em.System<GameTicker>();
                var sectorServices = em.System<SectorServiceSystem>();
                if (!em.EntityExists(sectorServices.GetServiceEntity()))
                    em.AddComponent<StationSectorServiceHostComponent>(em.SpawnEntity(null, publicMap.MapCoords));
                sector = em.GetComponent<SectorBankComponent>(sectorServices.GetServiceEntity());
                Assert.That(bank.TryGetBalance(SectorBankAccount.Frontier, out _), Is.True);

                var floor = publicMap.Tile.Tile;
                EntityUid Grid(Vector2 position)
                {
                    var grid = maps.CreateGridEntity(publicMap.MapId);
                    maps.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, floor);
                    transform.SetWorldPosition(grid.Owner, position);
                    return grid.Owner;
                }
                root = Grid(new Vector2(200, 300));
                fragment = Grid(new Vector2(210, 300));
                claimed = Grid(new Vector2(2000, 3000));
                var parent = em.AddComponent<LinkedLifecycleGridParentComponent>(root);
                parent.LinkedEntities.Add(fragment);
                em.AddComponent<LinkedLifecycleGridChildComponent>(fragment).LinkedUid = root;
                // Shared references/cycles must not enqueue a family twice or restore roots twice.
                em.AddComponent<LinkedLifecycleGridParentComponent>(fragment).LinkedEntities.Add(root);

                EntityUid Character(EntityUid grid)
                {
                    var uid = em.SpawnEntity("MobHuman", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
                    bank.SetConfirmedBankBalance(uid, 0);
                    return uid;
                }
                player = Character(root);
                fragmentPlayer = Character(fragment);
                em.AddComponent<ClaimableGridComponent>(claimed).ClaimedBy.Add(player);

                backpack = em.SpawnEntity("ClothingBackpack", new EntityCoordinates(root, Vector2.Zero));
                carriedCargo = em.SpawnEntity("DLTestRetirementValuable", new EntityCoordinates(root, Vector2.Zero));
                Assert.That(containers.Insert(backpack, containers.EnsureContainer<Container>(player, "retirement-held")), Is.True);
                Assert.That(containers.Insert(carriedCargo, containers.EnsureContainer<Container>(backpack, "retirement-cargo")), Is.True);
                var crate = em.SpawnEntity("CrateGenericSteel", new EntityCoordinates(root, new Vector2(.5f, .5f)));
                discarded.Add(crate);
                var lost = em.SpawnEntity("DLTestRetirementValuable", new EntityCoordinates(root, Vector2.Zero));
                Assert.That(containers.Insert(lost, containers.EnsureContainer<Container>(crate, "retirement-discard")), Is.True);
                discarded.Add(lost);

                if (stock != 0)
                {
                    var contained = containers.EnsureContainer<Container>(crate, "retirement-bulk-discard");
                    for (var i = 0; i < 64; i++)
                    {
                        var item = em.SpawnEntity("SheetSteel1", new EntityCoordinates(root, Vector2.Zero));
                        Assert.That(containers.Insert(item, contained), Is.True);
                        discarded.Add(item);
                    }
                    var tiles = new List<(Vector2i, Tile)>(stock);
                    for (var i = 0; i < stock; i++)
                        tiles.Add((new Vector2i(i % 64, i / 64), floor));
                    maps.SetTiles(root, em.GetComponent<MapGridComponent>(root), tiles);
                    for (var i = 0; i < stock; i++)
                        discarded.Add(em.SpawnEntity("SheetSteel1", new EntityCoordinates(root,
                            new Vector2(i % 64 + .5f, i / 64 + .5f))));
                }

                // Compare velocity with the exact former native detach/delete/reparent flow.
                var controlGrid = Grid(new Vector2(10000, 10000));
                var control = Character(controlGrid);
                var velocity = new Vector2(3, 4);
                physics.SetLinearVelocity(player, velocity);
                physics.SetLinearVelocity(control, velocity);
                var controlPosition = transform.GetWorldPosition(control);
                transform.DetachEntity(control);
                em.DeleteEntity(controlGrid);
                transform.SetCoordinates(control, new EntityCoordinates(publicMap.MapUid, controlPosition));
                var expectedVelocity = em.GetComponent<PhysicsComponent>(control).LinearVelocity;

                var positions = new Dictionary<EntityUid, Vector2>
                {
                    [player] = transform.GetWorldPosition(player),
                    [fragmentPlayer] = transform.GetWorldPosition(fragmentPlayer),
                };
                var rule = ticker.AddGameRule(Rule);
                var component = em.GetComponent<BluespaceErrorRuleComponent>(rule);
                // Runtime UIDs cannot be expressed in the prototype fixture. Populate only
                // this protected collection through reflection, without widening product access.
                var ruleGrids = (List<EntityUid>) typeof(BluespaceErrorRuleComponent)
                    .GetField(nameof(BluespaceErrorRuleComponent.GridsUid))!.GetValue(component)!;
                ruleGrids.AddRange([root, fragment, root, claimed]);
                expectedReward = (int) (em.System<PricingSystem>().AppraiseGrid(root,
                    uid => uid != player && uid != fragmentPlayer) * .25f);
                Assert.That(expectedReward, Is.GreaterThan(0));
                Assert.That(bank.TryGetBalance(SectorBankAccount.Frontier, out var balance), Is.True);
                var ledger = sector.AccountLedgerEntries.GetValueOrDefault((SectorBankAccount.Frontier, LedgerEntryType.BluespaceReward));
                mapCountBefore = LiveMaps();
                var gcBefore = GC.GetTotalPauseDuration();
                var start = RetirementClock.GetTimestamp();
                Assert.That(ticker.EndGameRule(rule), Is.True);
                endMs = RetirementClock.GetElapsedTime(start).TotalMilliseconds;
                endGcPause = GC.GetTotalPauseDuration() - gcBefore;

                privateMap = em.GetComponent<TransformComponent>(root).MapUid!.Value;
                Assert.That(privateMap, Is.Not.EqualTo(publicMap.MapUid));
                Assert.That(em.GetComponent<TransformComponent>(fragment).MapUid, Is.EqualTo(privateMap));
                Assert.That(maps.IsInitialized(privateMap), Is.False);
                Assert.That(maps.IsPaused(privateMap), Is.True);
                Assert.That(em.GetComponent<MetaDataComponent>(root).EntityPaused, Is.True);
                Assert.That(staged.IsPending(privateMap), Is.True);
                Assert.That(staged.PendingCount, Is.EqualTo(1));
                Assert.That(LiveMaps(), Is.EqualTo(mapCountBefore + 1));
                Assert.That(em.HasComponent<LinkedLifecycleGridParentComponent>(root), Is.False);
                Assert.That(em.HasComponent<LinkedLifecycleGridParentComponent>(fragment), Is.False);
                Assert.That(em.GetComponent<TransformComponent>(claimed).MapUid, Is.EqualTo(publicMap.MapUid));
                Assert.That(em.GetComponent<MetaDataComponent>(claimed).EntityPaused, Is.False);
                foreach (var (uid, position) in positions)
                {
                    var xform = em.GetComponent<TransformComponent>(uid);
                    Assert.That(xform.MapUid, Is.EqualTo(publicMap.MapUid));
                    Assert.That(xform.ParentUid, Is.EqualTo(publicMap.MapUid), "The old grid must no longer capture the restored player.");
                    Assert.That(Vector2.Distance(transform.GetWorldPosition(uid), position), Is.LessThan(.0001f));
                    Assert.That(em.GetComponent<MetaDataComponent>(uid).EntityPaused, Is.False);
                }
                Assert.That(em.GetComponent<PhysicsComponent>(player).LinearVelocity, Is.EqualTo(expectedVelocity));
                Assert.That(containers.IsEntityInContainer(backpack), Is.True);
                Assert.That(containers.IsEntityInContainer(carriedCargo), Is.True);
                Assert.That(bank.TryGetBalance(SectorBankAccount.Frontier, out var balanceAfter), Is.True);
                Assert.That(balanceAfter, Is.EqualTo(balance + expectedReward));
                rewardLedgerAfter = sector.AccountLedgerEntries.GetValueOrDefault((SectorBankAccount.Frontier, LedgerEntryType.BluespaceReward));
                Assert.That(rewardLedgerAfter, Is.EqualTo(ledger + expectedReward));
                Assert.That(ticker.EndGameRule(rule), Is.False);
                Assert.That(sector.AccountLedgerEntries.GetValueOrDefault((SectorBankAccount.Frontier, LedgerEntryType.BluespaceReward)), Is.EqualTo(rewardLedgerAfter));
                // Ownership cannot depend on the now-ended rule entity surviving cleanup.
                em.DeleteEntity(rule);

                while (staged.PendingCount != 0 && slices < 4096)
                {
                    var sliceStart = RetirementClock.GetTimestamp();
                    staged.Update(1f / 30f);
                    var duration = RetirementClock.GetElapsedTime(sliceStart).TotalMilliseconds;
                    totalSlicesMs += duration;
                    maximumSliceMs = Math.Max(maximumSliceMs, duration);
                    slices++;
                    if (stock > 128 && slices == 1)
                    {
                        Assert.That(em.EntityExists(root), Is.True, "A large published family must not be recursively deleted in its first slice.");
                        Assert.That(staged.PendingCount, Is.GreaterThan(0));
                    }
                }
                Assert.That(staged.PendingCount, Is.Zero);
                Assert.That(em.EntityExists(privateMap), Is.False);
                Assert.That(em.EntityExists(root), Is.False);
                Assert.That(em.EntityExists(fragment), Is.False);
                Assert.That(discarded.All(uid => !em.EntityExists(uid)), Is.True);
                Assert.That(LiveMaps(), Is.EqualTo(mapCountBefore));
                Assert.That(em.EntityExists(claimed), Is.True);
                Assert.That(em.EntityExists(player), Is.True);
                Assert.That(em.EntityExists(fragmentPlayer), Is.True);
                Assert.That(em.EntityExists(backpack), Is.True);
                Assert.That(em.EntityExists(carriedCargo), Is.True);
            });

            await pair.RunTicksSync(5);
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(staged.PendingCount, Is.Zero);
                Assert.That(LiveMaps(), Is.EqualTo(mapCountBefore));
                Assert.That(em.GetComponent<TransformComponent>(player).MapUid, Is.EqualTo(publicMap.MapUid));
                Assert.That(em.GetComponent<TransformComponent>(fragmentPlayer).MapUid, Is.EqualTo(publicMap.MapUid));
                Assert.That(sector.AccountLedgerEntries.GetValueOrDefault((SectorBankAccount.Frontier, LedgerEntryType.BluespaceReward)), Is.EqualTo(rewardLedgerAfter));
            });
            TestContext.Progress.WriteLine(FormattableString.Invariant(
                $"Native retirement microbenchmark: stock={stock} contained_stock={(stock == 0 ? 0 : 64)} end_ms={endMs:F3} end_gc_pause_ms={endGcPause.TotalMilliseconds:F3} deletion_slices={slices} deletion_total_ms={totalSlicesMs:F3} deletion_max_slice_ms={maximumSliceMs:F3} staged_budget_is_soft=True"));
        }
        finally
        {
            await pair.Server.WaitPost(() => config.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, previousLevel));
        }
        await pair.CleanReturnAsync();
    }
}
