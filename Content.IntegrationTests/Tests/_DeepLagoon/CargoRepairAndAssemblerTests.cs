using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Lathe;
using Content.Server.Power.Components;
using Content.Server.Stack;
using Content.Server.Storage.EntitySystems;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Access.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Events;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Research.Prototypes;
using Content.Shared.Station.Components;
using Robust.Server.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Numerics;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CargoRepairAndAssemblerTests
{
    [Test]
    public async Task CargoApprovalChecksCapacityAndPaymentAndDispatchKeepsPaidOrders()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cargo = entities.System<CargoSystem>();
            var station = entities.SpawnEntity(null, map.GridCoords);
            var db = entities.AddComponent<StationCargoOrderDatabaseComponent>(station);
            entities.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
            var console = entities.SpawnEntity("ComputerCargoOrders", map.GridCoords);
            entities.RemoveComponent<AccessReaderComponent>(console);
            var netConsole = entities.GetNetEntity(console);
            var actor = entities.SpawnEntity("MobHumanDummy", map.GridCoords);
            entities.EnsureComponent<BankAccountComponent>(actor);
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.PlayerMan.SetAttachedEntity(session, actor);
            var bank = entities.GetComponent<BankAccountComponent>(actor);
            var balance = bank.Balance;
            Assert.That(balance, Is.GreaterThanOrEqualTo(2));

            var first = new CargoOrderData(1, "CrateGenericSteel", "first", 1, db.Capacity, "test", "", netConsole)
            {
                Approved = true,
            };
            var second = new CargoOrderData(2, "CrateGenericSteel", "second", 1, 2, "test", "", netConsole);
            db.Orders.Add(first);
            db.Orders.Add(second);
            void Approve() => entities.EventBus.RaiseLocalEvent(console, new CargoConsoleApproveOrderMessage(2) { Actor = actor });

            Assert.That(cargo.FulfillNextOrder([netConsole], db, map.GridCoords, null), Is.True);
            Approve();
            Assert.That(second.Approved, Is.False);
            Assert.That(second.OrderQuantity, Is.EqualTo(2));
            Assert.That(bank.Balance, Is.EqualTo(balance));

            Assert.That(cargo.FulfillNextOrder([netConsole], db, map.GridCoords, null), Is.True);
            var otherConsole = entities.SpawnEntity("ComputerCargoOrders", map.GridCoords);
            entities.RemoveComponent<AccessReaderComponent>(otherConsole);
            entities.EventBus.RaiseLocalEvent(otherConsole, new CargoConsoleApproveOrderMessage(2) { Actor = actor });
            entities.EventBus.RaiseLocalEvent(otherConsole, new CargoConsoleRemoveOrderMessage(2) { Actor = actor });
            Assert.That(second.Approved, Is.False);
            Assert.That(db.Orders, Does.Contain(second));
            Assert.That(bank.Balance, Is.EqualTo(balance));

            entities.EnsureComponent<IronmanComponent>(actor);
            Approve();
            Assert.That(second.Approved, Is.False);
            Assert.That(bank.Balance, Is.EqualTo(balance));
            entities.RemoveComponent<IronmanComponent>(actor);

            Approve();
            Assert.That(second.Approved, Is.True);
            Assert.That(bank.Balance, Is.EqualTo(balance - 2));
            entities.EventBus.RaiseLocalEvent(console, new CargoConsoleRemoveOrderMessage(2) { Actor = actor });
            Assert.That(db.Orders, Does.Contain(second));

            while (first.NumDispatched < first.OrderQuantity)
            {
                Assert.That(cargo.FulfillNextOrder([netConsole], db, map.GridCoords, null), Is.True);
            }

            Assert.That(second.NumDispatched, Is.Zero);
            Assert.That(cargo.FulfillNextOrder([netConsole], db, map.GridCoords, null), Is.True);
            Assert.That(cargo.FulfillNextOrder([netConsole], db, map.GridCoords, null), Is.True);
            Assert.That(cargo.FulfillNextOrder([netConsole], db, map.GridCoords, null), Is.False);
            Assert.That(db.Orders, Is.Empty);
            Assert.That(bank.Balance, Is.EqualTo(balance - 2));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Ointment1", "RegenerativeMeshAssembler", "Leporazine", "Dermaline")]
    [TestCase("Brutepack1", "MedicatedSutureAssembler", "TranexamicAcid", "Cryptobiolin")]
    public async Task MedicalAssemblerConsumesOneUnitOfAlternateStack(
        string variant, string recipeId, string firstReagent, string secondReagent)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var storage = entities.System<EntityStorageSystem>();
            var lathe = entities.System<LatheSystem>();
            var chemistry = entities.System<SharedSolutionContainerSystem>();
            var stackSystem = entities.System<StackSystem>();
            var machine = entities.SpawnEntity("MedicalAssembler", map.GridCoords);
            var recipe = pair.Server.ResolveDependency<IPrototypeManager>().Index<LatheRecipePrototype>(recipeId);
            var beaker = entities.SpawnEntity("LargeBeaker", map.GridCoords);
            Assert.That(chemistry.TryGetDrainableSolution(beaker, out var solution, out _), Is.True);
            Assert.That(chemistry.TryAddReagent(solution!.Value, firstReagent, 20), Is.True);
            Assert.That(chemistry.TryAddReagent(solution.Value, secondReagent, 20), Is.True);
            Assert.That(entities.System<ItemSlotsSystem>().TryInsert(machine, "beaker_slot", beaker, null), Is.True);

            var cloth = entities.SpawnEntity("MaterialCloth", map.GridCoords);
            stackSystem.SetCount(cloth, 2);
            var stack = entities.SpawnEntity(variant, map.GridCoords);
            Assert.That(stackSystem.TryAdd(entities.SpawnEntity(variant, map.GridCoords), stack), Is.True);
            Assert.That(storage.Insert(cloth, machine), Is.True);
            Assert.That(storage.Insert(stack, machine), Is.True);
            Assert.That(lathe.CanProduce(machine, recipe), Is.True);

            entities.GetComponent<ApcPowerReceiverComponent>(machine).Powered = true;
            Assert.That(lathe.TryAddToQueue(machine, recipe, 1), Is.True);
            Assert.That(lathe.TryStartProducing(machine), Is.True);
            Assert.That(stackSystem.GetCount(stack), Is.EqualTo(1));
            Assert.That(stackSystem.GetCount(cloth), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShipRepairTransfersOriginalFromSplitFragmentAndDoesNotDuplicateIt()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var testMap = await pair.CreateTestMap();
        var original = NetEntity.Invalid;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>();
            var xform = entities.System<SharedTransformSystem>();
            var main = testMap.Grid;
            var fragment = map.CreateGridEntity(testMap.MapId);
            xform.SetCoordinates(fragment.Owner, new EntityCoordinates(testMap.MapUid, new Vector2(8, 0)));
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var target = new EntityCoordinates(main.Owner, new Vector2(1.5f, .5f));
            var fragmentTile = new Vector2i(0, 0);
            map.SetTile(main, new Vector2i(1, 0), tile);
            map.SetTile(fragment, fragmentTile, tile);
            var actor = entities.SpawnEntity("MobHuman", testMap.GridCoords);
            var tool = entities.SpawnEntity("ShipRepairDevice", testMap.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickup(actor, tool, checkActionBlocker: false), Is.True);
            var wall = entities.SpawnEntity("WallSolid", target);
            entities.System<SharedShipRepairSystem>().GenerateRepairData(main);
            original = entities.GetNetEntity(wall);
            Assert.That(entities.GetComponent<ShipRepairDataComponent>(main).Chunks.Values
                .SelectMany(chunk => chunk.Entities.Values).Any(spec => spec.OriginalEntity == original), Is.True);
            Assert.That(xform.AnchorEntity((wall, null, null), fragment, fragmentTile), Is.True);
            var worldClick = xform.WithEntityId(target, testMap.MapUid);
            var toolComp = entities.GetComponent<ShipRepairToolComponent>(tool);

            void Repair() => entities.EventBus.RaiseLocalEvent(tool, new AfterInteractEvent(actor, tool, null, worldClick, true));
            Repair();
            Assert.That(toolComp.DoAfters, Is.Empty, "A different grid is not automatically a fragment.");

            var split = new PostGridSplitEvent(main.Owner, fragment.Owner);
            entities.EventBus.RaiseLocalEvent(main.Owner, ref split, true);
            Assert.That(entities.GetComponent<ShipRepairFragmentComponent>(fragment).SourceGrids,
                Does.Contain(entities.GetNetEntity(main)));
            Repair();
            Assert.That(toolComp.DoAfters, Has.Count.EqualTo(1));
        });

        await pair.RunTicksSync(100);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var wallCount = entities.EntityQuery<TransformComponent, MetaDataComponent>(true)
                .Count(tuple => tuple.Item1.MapUid == testMap.MapUid && tuple.Item2.EntityPrototype?.ID == "WallSolid");
            Assert.That(wallCount, Is.EqualTo(1));
            var restored = entities.GetComponent<TransformComponent>(entities.GetEntity(original));
            Assert.That(restored.GridUid, Is.EqualTo(testMap.Grid.Owner));
            Assert.That(restored.Anchored, Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
