using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Shared.Cargo;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared.Stacks;
using NUnit.Framework;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class NpcProcurementTests
{
    private static T Complete<T>(ITaskManager tasks, Task<T> task)
    { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
    private static bool Busy(CargoSystem system, EntityUid console)
        => ((IEnumerable)typeof(CargoSystem).GetField("_economyConsoles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!).Cast<EntityUid>().Contains(console);

    [Test]
    public async Task FiftyUraniumSheetsCanBeSoldAtTheLowRateWithOneActivePlayersHalfHourFunding()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("uranium_supplier");
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => preferences.TryGetCachedPreferences(player.UserId, out _));
        var database = server.ResolveDependency<IServerDbManager>();
        var tasks = server.ResolveDependency<ITaskManager>();
        EntityUid console = default, material = default, body = default;
        SectorEconomySystem economy = null!;
        CargoSystem cargo = null!;
        EconomyAccount before = null!;
        var slot = 0;
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            economy = e.System<SectorEconomySystem>(); cargo = e.System<CargoSystem>();
            slot = preferences.GetPreferences(player.UserId).SelectedCharacterIndex;
            before = Complete(tasks, database.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(player, body);
            console = e.SpawnEntity("ComputerPalletConsoleNFVeryLowMarket", map.GridCoords);
            var pallet = e.SpawnEntity("CargoPalletSell", map.GridCoords);
            e.System<SharedTransformSystem>().AnchorEntity(console); e.System<SharedTransformSystem>().AnchorEntity(pallet);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, CargoPalletConsoleUiKey.Sale, body);
            material = e.SpawnEntity("SheetUranium", map.GridCoords);
            e.System<StackSystem>().SetCount(material, 50);
        });
        await PoolManager.WaitUntil(server, () => economy.Ready);
        await server.WaitAssertion(() =>
        {
            var epoch = Guid.NewGuid();
            var start = SectorEconomySystem.UtcNow + 1000;
            for (var minute = 0; minute <= 30; minute++)
                Complete(tasks, database.SampleSectorEconomyAsync(epoch, start + minute * 60000L, 1, economy.Policy, []));
            typeof(CargoSystem).GetMethod("BeginEconomySale", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cargo, [console, e.GetComponent<CargoPalletConsoleComponent>(console), body]);
        });
        await PoolManager.WaitUntil(server, () => !Busy(cargo, console));
        await server.WaitAssertion(() =>
        {
            Assert.That(e.EntityExists(material), Is.False);
            var after = Complete(tasks, database.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            var cash = e.EntityQuery<StackComponent, TransformComponent>().Where(p => p.Item1.StackTypeId == "Credit" && p.Item2.GridUid == map.Grid).Sum(p => p.Item1.Count);
            Assert.That(after.Bank, Is.EqualTo(before.Bank));
            Assert.That(cash, Is.InRange(1, 1000));
            Assert.That(after.Finance.Spent, Is.GreaterThanOrEqualTo(cash));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExistingPalletReturnsUnaffordableGoodsAndAlwaysPaysCashWithoutCreditingTheBank()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("procurement_supplier");
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => preferences.TryGetCachedPreferences(player.UserId, out _));
        var database = server.ResolveDependency<IServerDbManager>();
        var tasks = server.ResolveDependency<ITaskManager>();
        var slot = 0;
        EntityUid console = default, material = default, body = default;
        CargoSystem cargo = null!;
        SectorEconomySystem economy = null!;
        EconomyAccount before = null!;
        var previousCash = 0;
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            economy = e.System<SectorEconomySystem>(); cargo = e.System<CargoSystem>();
            slot = preferences.GetPreferences(player.UserId).SelectedCharacterIndex;
            before = Complete(tasks, database.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(player, body);
            console = e.SpawnEntity("ComputerPalletConsoleNFHighMarket", map.GridCoords);
            var pallet = e.SpawnEntity("CargoPalletSell", map.GridCoords);
            e.System<SharedTransformSystem>().AnchorEntity(console); e.System<SharedTransformSystem>().AnchorEntity(pallet);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, CargoPalletConsoleUiKey.Sale, body);
            material = e.SpawnEntity("SheetPlasteel", map.GridCoords);
            e.GetComponent<StackComponent>(material).MaxCountOverride = 100000;
            e.System<StackSystem>().SetCount(material, 100000);
            Assert.That(e.GetComponent<StackComponent>(material).Count, Is.EqualTo(100000));
        });
        await PoolManager.WaitUntil(server, () => economy.Ready);
        await server.WaitAssertion(() =>
        {
            var epoch = Guid.NewGuid();
            var start = SectorEconomySystem.UtcNow + 1000;
            for (var minute = 0; minute <= 60; minute++)
                Complete(tasks, database.SampleSectorEconomyAsync(epoch, start + minute * 60000L, 30, economy.Policy, []));
            typeof(CargoSystem).GetMethod("BeginEconomySale", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cargo, [console, e.GetComponent<CargoPalletConsoleComponent>(console), body]);
        });
        await PoolManager.WaitUntil(server, () => !Busy(cargo, console));
        await server.WaitAssertion(() =>
        {
            Assert.That(e.EntityExists(material), Is.True);
            Assert.That(e.GetComponent<TransformComponent>(material).GridUid, Is.EqualTo(map.Grid.Owner));
            Assert.That(e.GetComponent<StackComponent>(material).Count, Is.EqualTo(100000));
            var after = Complete(tasks, database.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            Assert.That(after.Bank, Is.EqualTo(before.Bank)); Assert.That(after.Finance.Spent, Is.Zero);
            e.System<StackSystem>().SetCount(material, 3);
            typeof(CargoSystem).GetMethod("BeginEconomySale", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cargo, [console, e.GetComponent<CargoPalletConsoleComponent>(console), body]);
        });
        await PoolManager.WaitUntil(server, () => !Busy(cargo, console));
        await server.WaitAssertion(() =>
        {
            Assert.That(e.EntityExists(material), Is.False);
            var after = Complete(tasks, database.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            Assert.That(after.Bank, Is.EqualTo(before.Bank));
            previousCash = e.EntityQuery<StackComponent, TransformComponent>().Where(p => p.Item1.StackTypeId == "Credit" && p.Item2.GridUid == map.Grid).Sum(p => p.Item1.Count);
            Assert.That(previousCash, Is.InRange(1, 8000));
            Assert.That(after.Finance.Spent, Is.GreaterThanOrEqualTo(previousCash));
            before = after;
            e.EnsureComponent<IronmanComponent>(body);
            material = e.SpawnEntity("SheetPlasteel", map.GridCoords);
            e.System<StackSystem>().SetCount(material, 3);
            typeof(CargoSystem).GetMethod("BeginEconomySale", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cargo, [console, e.GetComponent<CargoPalletConsoleComponent>(console), body]);
        });
        await PoolManager.WaitUntil(server, () => !Busy(cargo, console));
        await server.WaitAssertion(() =>
        {
            Assert.That(e.EntityExists(material), Is.False);
            var after = Complete(tasks, database.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            Assert.That(after.Bank, Is.EqualTo(before.Bank), "Ironman uses the same cash settlement as other characters.");
            Assert.That(after.Finance.Spent, Is.GreaterThan(before.Finance.Spent), "Cash payout consumes the same finite budget.");
            var cash = e.EntityQuery<StackComponent, TransformComponent>().Where(p => p.Item1.StackTypeId == "Credit" && p.Item2.GridUid == map.Grid).Sum(p => p.Item1.Count);
            Assert.That(cash, Is.GreaterThan(previousCash));
            Assert.That(cash - previousCash, Is.LessThanOrEqualTo(8000), "Returned cash principal must not be multiplied.");
            previousCash = cash;
            before = after;
            // A repeated click with only the cash left on the pallet cannot mint another payment.
            typeof(CargoSystem).GetMethod("BeginEconomySale", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cargo, [console, e.GetComponent<CargoPalletConsoleComponent>(console), body]);
        });
        await PoolManager.WaitUntil(server, () => !Busy(cargo, console));
        await server.WaitAssertion(() =>
        {
            var after = Complete(tasks, database.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            Assert.That(after.Bank, Is.EqualTo(before.Bank));
            Assert.That(after.Finance.Spent, Is.EqualTo(before.Finance.Spent));
            Assert.That(e.EntityQuery<StackComponent, TransformComponent>().Where(p => p.Item1.StackTypeId == "Credit" && p.Item2.GridUid == map.Grid).Sum(p => p.Item1.Count), Is.EqualTo(previousCash));
        });
        await pair.CleanReturnAsync();
    }
}
