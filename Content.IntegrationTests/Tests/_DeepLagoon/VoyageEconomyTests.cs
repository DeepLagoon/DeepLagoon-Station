using System.Numerics;
using Robust.Shared.Map;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Shared.DeviceLinking;
using Content.Shared.Cargo.Components;
using Robust.Shared.Containers;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._DeepLagoon.Voyages;
using Content.Server._NF.Bank;
using Content.Server._NF.SectorServices;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Server.Power.Components;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Mobs.Components;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;
[TestFixture, NonParallelizable]
public sealed class VoyageEconomyTests
{
    private static T Complete<T>(ITaskManager tasks, Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
    private static void Request(VoyageSystem system, ICommonSession session, VoyageRequest request) =>
        typeof(VoyageSystem).GetMethod("OnRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(system, [request, new EntitySessionEventArgs(session)]);
    private static T Field<T>(object system, string name) => (T)system.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
    private static void ForceSurvival(IServerDbManager manager, Guid id)
    {
        // Test-only control of the persisted random draws, with all gameplay settlement paths intact.
        var backend = Field<ServerDbBase>(manager, "_db");
        var options = Field<Func<DbContextOptions<SqliteServerDbContext>>>(backend, "_options")();
        using var db = new SqliteServerDbContext(options);
        var row = db.LagoonEconomyRecords.Single(r => r.Id == "voyage:job:" + id.ToString("N"));
        var job = JsonSerializer.Deserialize<VoyageJob>(row.Data)!;
        row.Data = JsonSerializer.Serialize(job with { OutRoll = .999999, BackRoll = .999999 }); db.SaveChanges();
    }
    [Test]
    public async Task RealCargoAndFundedBountyAreConsumedOnceWithoutADuplicateFoodBudgetDebit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server; var e = server.EntMan; var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("voyage_supplier");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(player.UserId, out _));
        var db = server.ResolveDependency<IServerDbManager>(); var tasks = server.ResolveDependency<ITaskManager>();
        var system = e.System<VoyageSystem>();
        EntityUid console = default; SectorBankComponent treasury = null!; StationCargoBountyDatabaseComponent bounties = null!;
        VoyageCrewRecord crew = null!; EconomyAccount before = null!; CargoBountyData bounty = default;
        EntityUid[] meals = [];
        var crewId = Guid.NewGuid();
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            server.CfgMan.SetCVar(VoyageCVars.TravelTimeScale, .01f);
            var services = e.System<SectorServiceSystem>();
            if (!e.EntityExists(services.GetServiceEntity())) e.EnsureComponent<StationSectorServiceHostComponent>(e.SpawnEntity(null, map.GridCoords));
            treasury = e.GetComponent<SectorBankComponent>(services.GetServiceEntity());
#pragma warning disable RA0002
            treasury.Accounts[SectorBankAccount.Frontier].Balance = 100000;
#pragma warning restore RA0002
            var slot = prefs.GetPreferences(player.UserId).SelectedCharacterIndex;
            before = Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            var actor = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<MobStateComponent>(actor); e.EnsureComponent<BankAccountComponent>(actor);
            server.PlayerMan.SetAttachedEntity(player, actor); e.System<BankSystem>().SetConfirmedBankBalance(actor, before.Bank);
            console = e.SpawnEntity("ComputerCargoBounty", map.GridCoords);
            var power = e.GetComponent<ApcPowerReceiverComponent>(console); power.NeedsPower = false; power.Powered = true;
            var pallet = e.SpawnEntity("CargoPalletSell", map.GridCoords);
            e.System<SharedTransformSystem>().AnchorEntity(console); e.System<SharedTransformSystem>().AnchorEntity(pallet);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, CargoConsoleUiKey.Bounty, actor);
            var station = e.SpawnEntity(null, map.GridCoords); bounties = e.EnsureComponent<StationCargoBountyDatabaseComponent>(station); bounties.MaxBounties = 0;
            var proto = server.ResolveDependency<IPrototypeManager>().Index<CargoBountyPrototype>("DLEconomyFoodBurger");
            bounty = new CargoBountyData(proto, 1) with { FundedReward = 4500, FoodAccount = SectorBankAccount.Frontier };
            Assert.That(e.System<BankSystem>().TrySectorWithdraw(SectorBankAccount.Frontier, 4500, LedgerEntryType.StationWithdrawalBounty), Is.True);
            bounties.Bounties.Add(bounty);
            meals = Enumerable.Range(0, 2).Select(_ => e.SpawnEntity("FoodBurgerPlain", map.GridCoords)).ToArray();
        });
        await PoolManager.WaitUntil(server, () => Field<bool>(system, "_ready"));
        await server.WaitAssertion(() => Request(system, player, new() { Console = e.GetNetEntity(console), Action = VoyageAction.Hire, Offer = Field<VoyageWorld>(system, "_world").Offers.First(o => o.Type == "cargo" && o.Level == 1).Id, Operation = crewId }));
        await PoolManager.WaitUntil(server, () => !Field<System.Collections.Generic.HashSet<Guid>>(system, "_busy").Contains(player.UserId.UserId));
        await server.WaitAssertion(() =>
        {
            var state = Complete(tasks, db.ReadVoyagesAsync(player.UserId.UserId, before.Profile)); crew = state.Crews.Single();
            Assert.That(state.Bank, Is.EqualTo(before.Bank - 6000));
        });
        async Task Dispatch()
        {
            await Task.Delay(300);
            await server.WaitAssertion(() =>
            {
                var station = e.EntityQuery<StationCargoBountyDatabaseComponent>().Single(c => c == bounties).Owner;
                Request(system, player, new() { Console = e.GetNetEntity(console), Action = VoyageAction.Dispatch, Crew = crew.Id,
                    Order = e.GetNetEntity(station) + ":" + bounty.Instance.ToString("N"), Destination = "helios", Operation = Guid.NewGuid() });
            });
            await PoolManager.WaitUntil(server, () => !Field<System.Collections.Generic.HashSet<Guid>>(system, "_busy").Contains(player.UserId.UserId));
        }
        await Dispatch();
        await server.WaitAssertion(() =>
        {
            Assert.That(Complete(tasks, db.ActiveVoyagesAsync()), Is.Empty);
            Assert.That(bounties.Bounties, Has.Count.EqualTo(1));
            Assert.That(meals.All(e.EntityExists), Is.True);
            Assert.That(treasury.Accounts[SectorBankAccount.Frontier].Balance, Is.EqualTo(95500));
            e.SpawnEntity("FoodBurgerPlain", map.GridCoords); e.SpawnEntity("FoodBurgerPlain", map.GridCoords);
        });
        var funded = bounty;
        await server.WaitAssertion(() =>
        {
            bounties.Bounties.Clear(); bounty = bounty with { Instance = Guid.NewGuid(), FundedReward = -1 };
            bounties.Bounties.Add(bounty);
#pragma warning disable RA0002
            treasury.Accounts[SectorBankAccount.Frontier].Balance = 0;
#pragma warning restore RA0002
        });
        await Dispatch();
        await server.WaitAssertion(() =>
        {
            Assert.That(Complete(tasks, db.ActiveVoyagesAsync()), Is.Empty);
            Assert.That(bounties.Bounties, Has.Count.EqualTo(1));
            Assert.That(e.EntityQuery<MetaDataComponent, TransformComponent>().Count(p => p.Item1.EntityPrototype?.ID == "FoodBurgerPlain" && p.Item2.GridUid == map.Grid), Is.EqualTo(4));
            bounties.Bounties.Clear(); bounty = funded; bounties.Bounties.Add(funded);
#pragma warning disable RA0002
            treasury.Accounts[SectorBankAccount.Frontier].Balance = 95500;
#pragma warning restore RA0002
        });
        await Dispatch();
        await PoolManager.WaitUntil(server, async () => (await db.ActiveVoyagesAsync()).Length == 1);
        await server.WaitRunTicks(3);
        await server.WaitAssertion(() =>
        {
            var job = Complete(tasks, db.ActiveVoyagesAsync()).Single(); ForceSurvival(db, job.Id);
            Assert.That(job.Reward, Is.EqualTo(4500)); Assert.That(job.Delivered, Is.False); Assert.That(job.Return - job.Arrive, Is.EqualTo(job.Arrive - job.Depart));
            Assert.That(bounties.Bounties, Is.Empty);
            Assert.That(e.EntityQuery<MetaDataComponent, TransformComponent>().Count(p => p.Item1.EntityPrototype?.ID == "FoodBurgerPlain" && p.Item2.GridUid == map.Grid), Is.EqualTo(1));
            Assert.That(treasury.Accounts[SectorBankAccount.Frontier].Balance, Is.EqualTo(95500), "The meal order was already reserved at publication.");
            Assert.That(Complete(tasks, db.ReadVoyagesAsync(player.UserId.UserId, before.Profile)).Bank, Is.EqualTo(before.Bank - 6000));
        });
        var scheduled = (await db.ActiveVoyagesAsync()).Single();
        await Task.Delay((int)Math.Max(1, scheduled.Arrive - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30));
        await PoolManager.WaitUntil(server, async () => (await db.ActiveVoyagesAsync()).SingleOrDefault()?.Delivered == true);
        await server.WaitAssertion(() => Assert.That(Complete(tasks, db.ReadVoyagesAsync(player.UserId.UserId, before.Profile)).Bank, Is.EqualTo(before.Bank - 6000), "Delivery alone must not pay."));
        await server.WaitAssertion(() => Assert.That(e.System<BankSystem>().BeginExchangePayment(player.UserId), Is.True));
        try
        {
            await Task.Delay((int)Math.Max(1, scheduled.Return - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30));
            await server.WaitRunTicks(3);
            await server.WaitAssertion(() =>
            {
                Assert.That(Complete(tasks, db.ActiveVoyagesAsync()), Has.Length.EqualTo(1));
                Assert.That(Complete(tasks, db.ReadVoyagesAsync(player.UserId.UserId, before.Profile)).Bank, Is.EqualTo(before.Bank - 6000));
            });
        }
        finally { await server.WaitAssertion(() => e.System<BankSystem>().EndExchangePayment(player.UserId)); }
        await Task.Delay(2100);
        await PoolManager.WaitUntil(server, async () => (await db.ActiveVoyagesAsync()).Length == 0);
        await server.WaitAssertion(() =>
        {
            Assert.That(Complete(tasks, db.ReadVoyagesAsync(player.UserId.UserId, before.Profile)).Bank, Is.EqualTo(before.Bank - 6000 + 4500));
            Assert.That(bounties.History, Has.Count.EqualTo(1));
            var notices = Complete(tasks, db.ReadCharacterNotificationsAsync(player.UserId.UserId, before.Profile, false));
            Assert.That(notices.Select(n => n.Title), Is.EquivalentTo(new[] { "Рейс отправлен", "Рейс достиг цели", "Рейс вернулся" }));
            Assert.That(notices.Single(n => n.Title == "Рейс вернулся").Amount, Is.EqualTo(4500));
        });
        // A real remote purchase remains virtual until the crew returns, then uses the linked telepad exactly once.
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(RecoveryCVars.Scope, "voyage-import-" + Guid.NewGuid().ToString("N"));
            server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
        });
        var recovery = e.System<CrashRecoverySystem>();
        await PoolManager.WaitUntil(server, () => recovery.Ready && recovery.ExternalDeliveriesReady, 3600);
        VoyageJob import = null!; EntityUid pad = default;
        await server.WaitAssertion(() =>
        {
            var consolePower = e.GetComponent<ApcPowerReceiverComponent>(console); consolePower.NeedsPower = false; consolePower.Powered = true;
            for (var x = -2; x <= 4; x++)
                for (var y = -2; y <= 2; y++) e.System<SharedMapSystem>().SetTile(map.Grid.Owner, map.Grid.Comp, new EntityCoordinates(map.Grid, x, y), map.Tile.Tile);
            pad = e.SpawnEntity("CargoTelepad", new EntityCoordinates(map.Grid, 2, 0)); e.System<SharedTransformSystem>().AnchorEntity(pad);
            var power = e.GetComponent<ApcPowerReceiverComponent>(pad); power.NeedsPower = false; power.Powered = true;
#pragma warning disable RA0002
            e.EnsureComponent<DeviceLinkSourceComponent>(console).Outputs["OrderSender"] = new() { pad };
#pragma warning restore RA0002
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            import = new(Guid.NewGuid(), player.UserId.UserId, before.Profile, before.Slot, crew.Id, "", "helios", "Консоль",
                ["colossus", "helios"], "2 ящика", 0, 0, now - 20000, now - 10000, now - 1, false,
                Cycle: Field<VoyageWorld>(system, "_world").Cycle, Kind: 2, Product: "CrateGenericSteel", Quantity: 2,
                PurchaseCost: 2000, OutRoll: .999999, BackRoll: .999999);
            var commit = db.CommitVoyageAsync(import, null); tasks.BlockWaitOnTask(commit); commit.GetAwaiter().GetResult();
            Assert.That(Complete(tasks, db.AdvanceVoyageAsync(import.Id, now)).Paid, Is.True);
        });
        var cratesBefore = 0;
        await server.WaitAssertion(() =>
        {
            cratesBefore = e.EntityQuery<MetaDataComponent>().Count(m => m.EntityPrototype?.ID == "CrateGenericSteel");
            Request(system, player, new() { Console = e.GetNetEntity(console), Action = VoyageAction.ClaimCargo, Operation = import.Id });
        });
        await PoolManager.WaitUntil(server, () => !Field<System.Collections.Generic.HashSet<Guid>>(system, "_busy").Contains(player.UserId.UserId));
        await server.WaitRunTicks(3);
        await server.WaitAssertion(() =>
        {
            Assert.That(Complete(tasks, db.ReadVoyagesAsync(player.UserId.UserId, before.Profile)).Parcels, Is.Empty);
            Assert.That(Complete(tasks, db.ReadVoyagesAsync(player.UserId.UserId, before.Profile)).Bank, Is.EqualTo(before.Bank - 6000 + 4500 - 2000));
            var crates = e.EntityQuery<MetaDataComponent, TransformComponent>().Where(p => p.Item1.EntityPrototype?.ID == "CrateGenericSteel").ToArray();
            Assert.That(crates.Length, Is.EqualTo(cratesBefore + 2));
            Assert.That(crates.All(p => p.Item2.GridUid == map.Grid && !e.System<SharedContainerSystem>().IsEntityInContainer(p.Item1.Owner)), Is.True,
                string.Join("; ", crates.Select(p => $"parent={p.Item2.ParentUid} grid={p.Item2.GridUid} map={p.Item2.MapUid} coords={p.Item2.Coordinates} container={e.System<SharedContainerSystem>().IsEntityInContainer(p.Item1.Owner)}")));
            Assert.That(crates.All(p => Vector2.Distance(p.Item2.Coordinates.Position, e.GetComponent<TransformComponent>(pad).Coordinates.Position) < 1), Is.True, "The linked pad, not the console, is the delivery location.");
        });
        await Task.Delay(300);
        await server.WaitAssertion(() => Request(system, player, new() { Console = e.GetNetEntity(console), Action = VoyageAction.ClaimCargo, Operation = import.Id }));
        await PoolManager.WaitUntil(server, () => !Field<System.Collections.Generic.HashSet<Guid>>(system, "_busy").Contains(player.UserId.UserId));
        await server.WaitAssertion(() => Assert.That(e.EntityQuery<MetaDataComponent>().Count(m => m.EntityPrototype?.ID == "CrateGenericSteel"), Is.EqualTo(cratesBefore + 2)));
        await recovery.CloseNormally(); await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }
}
