using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server.Power.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Disposal.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Construction.Components;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Storage;
using Content.Shared.Stunnable;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunRemainingItemsTests
{
    [Test]
    public async Task PoweredDishDriveCollectsEmptyDishesAndSendsTrashToDisposals()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid drive = default, glass = default, fullGlass = default, trash = default, bin = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            for (var x = -2; x <= 6; x++)
            for (var y = -2; y <= 2; y++)
                maps.SetTile(map.Grid, new Robust.Shared.Maths.Vector2i(x, y), map.Tile.Tile);
            drive = em.SpawnEntity("BitrunDishDrive", map.GridCoords);
            glass = em.SpawnEntity("DrinkGlass", map.GridCoords.Offset(Vector2.UnitX));
            fullGlass = em.SpawnEntity("DrinkGlass", map.GridCoords.Offset(Vector2.UnitY));
            var solutions = em.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(fullGlass, "drink", out var solution), Is.True);
            Assert.That(solutions.TryAddReagent(solution!.Value, "Water", 5), Is.True);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.System<SharedContainerSystem>().IsEntityInContainer(glass), Is.False, "Suction needs machine power.");
            em.RemoveComponent<ApcPowerReceiverComponent>(drive);
            bin = em.SpawnEntity("DisposalUnit", map.GridCoords.Offset(new Vector2(5, 0)));
            Assert.That(em.GetComponent<TransformComponent>(bin).Anchored, Is.True);
            trash = em.SpawnEntity("ShardGlass", map.GridCoords.Offset(-Vector2.UnitX));
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 3);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var containers = em.System<SharedContainerSystem>();
            Assert.That(containers.GetContainer(drive, "bitrun-dishes").ContainedEntities, Does.Contain(glass));
            Assert.That(containers.IsEntityInContainer(fullGlass), Is.False, "The source refuses to collect non-empty drinks.");
            Assert.That(em.GetComponent<DisposalUnitComponent>(bin).Container.ContainedEntities, Does.Contain(trash));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AbductorBatonRejectsOutsidersThenStunsSleepsAndCuffs()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0.5f)));
            em.AddComponent<GodmodeComponent>(user);
            target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.9f, 0.5f)));
            var baton = em.SpawnEntity("BitrunSourceObjItemMeleeBatonAbductor", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, baton), Is.True);
            void Use()
            {
                var interact = new AfterInteractEvent(user, baton, target, map.GridCoords, true);
                em.EventBus.RaiseLocalEvent(baton, interact);
                Assert.That(interact.Handled, Is.True);
            }
            Use();
            Assert.That(em.HasComponent<StunnedComponent>(target), Is.False);
            em.AddComponent<BitrunAbductorActorComponent>(user);
            Use();
            Assert.That(em.HasComponent<StunnedComponent>(target), Is.True);
            void Next()
            {
                var mode = new UseInHandEvent(user);
                em.EventBus.RaiseLocalEvent(baton, mode);
                Assert.That(mode.Handled, Is.True);
            }
            Next(); Use();
            Assert.That(em.HasComponent<ForcedSleepingComponent>(target), Is.True);
            Next(); Use();
            Assert.That(em.GetComponent<BitrunAbductorBatonComponent>(baton).Target, Is.EqualTo(target));
            Assert.That(em.GetComponent<CuffableComponent>(target).CuffedHandCount, Is.Zero);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 4);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<CuffableComponent>(target).CuffedHandCount, Is.EqualTo(2)));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BalloonBoxContainsTwentyFourAndDifferentColorsFormTheSourceAnimal()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var box = em.SpawnEntity("BitrunSourceObjItemStorageBoxBalloons", map.GridCoords);
            var contents = em.GetComponent<StorageComponent>(box).Container.ContainedEntities;
            Assert.That(contents.Count, Is.EqualTo(24));
            Assert.That(contents.All(em.HasComponent<BitrunLongBalloonComponent>), Is.True);
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            em.AddComponent<BitrunBalloonArtistComponent>(user);
            var first = em.SpawnEntity("BitrunSourceObjItemToyBalloonLong", map.GridCoords);
            var second = em.SpawnEntity("BitrunSourceObjItemToyBalloonLong", map.GridCoords);
            em.GetComponent<BitrunLongBalloonComponent>(first).Color = "red";
            em.GetComponent<BitrunLongBalloonComponent>(second).Color = "blue";
            var combine = new InteractUsingEvent(user, second, first, map.GridCoords);
            em.EventBus.RaiseLocalEvent(first, combine);
            Assert.That(combine.Handled, Is.True);
            Assert.That(em.EntityQuery<MetaDataComponent>().Count(meta => meta.EntityPrototype?.ID == "BitrunSourceObjItemToyBalloonAnimalGuy"), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SuitVoucherOffersAllSixSourceChoicesAndCannotBeRedeemedTwice()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            var voucher = em.SpawnEntity("BitrunSourceObjItemSuitVoucher", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, voucher), Is.True);
            var terminal = em.SpawnEntity("BitrunSourceObjMachineryComputerOrderConsoleMining", map.GridCoords);
            if (em.HasComponent<ApcPowerReceiverComponent>(terminal)) em.RemoveComponent<ApcPowerReceiverComponent>(terminal);
            var comp = em.GetComponent<BitrunEquipmentVoucherComponent>(voucher);
            Assert.That(comp.Rewards.Count, Is.EqualTo(6));
            var verbs = em.System<SharedVerbSystem>().GetLocalVerbs(terminal, user, typeof(InteractionVerb));
            foreach (var reward in comp.Rewards)
                Assert.That(verbs.Any(verb => verb.Text == Robust.Shared.Localization.Loc.GetString(reward.Name)), Is.True, reward.Name.ToString());
            var wanted = comp.Rewards[0].Items[0].Id;
            var count = em.EntityQuery<MetaDataComponent>().Count(meta => meta.EntityPrototype?.ID == wanted);
            Assert.That(em.System<BitrunVoucherSystem>().Redeem(terminal, voucher, user, 0), Is.True);
            Assert.That(em.EntityQuery<MetaDataComponent>().Count(meta => meta.EntityPrototype?.ID == wanted), Is.EqualTo(count + 1));
            Assert.That(em.System<BitrunVoucherSystem>().Redeem(terminal, voucher, user, 0), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DishBoardBuildTargetStoresAndReturnsDishes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var board = em.SpawnEntity("BitrunSourceObjItemCircuitboardMachineDishDrive", map.GridCoords);
            var blueprint = em.GetComponent<MachineBoardComponent>(board);
            Assert.That(blueprint.Requirements["Manipulator"], Is.EqualTo(1));
            Assert.That(blueprint.Requirements["MatterBin"], Is.EqualTo(2));
            var drive = em.SpawnEntity(blueprint.Prototype, map.GridCoords);
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            var glass = em.SpawnEntity("DrinkGlass", map.GridCoords);
            var insert = new InteractUsingEvent(user, glass, drive, map.GridCoords);
            em.EventBus.RaiseLocalEvent(drive, insert);
            Assert.That(insert.Handled, Is.True);
            Assert.That(em.System<SharedContainerSystem>().IsEntityInContainer(glass), Is.True);
            var take = new ActivateInWorldEvent(user, drive, true);
            em.EventBus.RaiseLocalEvent(drive, take);
            Assert.That(take.Handled, Is.True);
            Assert.That(em.System<SharedHandsSystem>().IsHolding(user, glass, out _), Is.True);
            var suction = em.GetComponent<BitrunDishDriveBoardComponent>(board).Suction;
            var toggle = new UseInHandEvent(user);
            em.EventBus.RaiseLocalEvent(board, toggle);
            Assert.That(em.GetComponent<BitrunDishDriveBoardComponent>(board).Suction, Is.Not.EqualTo(suction));
        });
        await pair.CleanReturnAsync();
    }
}
