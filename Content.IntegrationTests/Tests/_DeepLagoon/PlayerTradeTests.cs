using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server._DeepLagoon.CryptoFarm;
using Content.Server._DeepLagoon.Economy;
using Content.Server._NF.Bank;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Research.Disk;
using Content.Server.Storage.Components;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._NF.Market;
using Content.Shared.Storage;
using Content.Shared.Mobs.Components;
using NUnit.Framework;
using Robust.Shared.Asynchronous;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PlayerTradeTests
{
    private static void Request(PlayerTradeSystem system, ICommonSession session, TradeRequest request)
        => typeof(PlayerTradeSystem).GetMethod("OnRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(system, [request, new EntitySessionEventArgs(session)]);
    private static IDictionary Accesses(PlayerTradeSystem system)
        => (IDictionary)typeof(PlayerTradeSystem).GetField("_access", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
    private static Guid Token(PlayerTradeSystem system, NetUserId user)
    { var access = Accesses(system)[user]!; return (Guid)access.GetType().GetProperty("Token")!.GetValue(access)!; }
    private static T Complete<T>(ITaskManager tasks, Task<T> task)
    { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }

    [Test]
    public async Task ExistingMarketConsoleTransfersAnOfflineSellersWholeCrateExactlyOnceWithCustomContents()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var seller = await server.AddDummySession("market_seller");
        var buyer = await server.AddDummySession("market_buyer");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(seller.UserId, out _) && prefs.TryGetCachedPreferences(buyer.UserId, out _));
        var db = server.ResolveDependency<IServerDbManager>();
        var tasks = server.ResolveDependency<ITaskManager>();
        var scope = "trade" + Guid.NewGuid().ToString("N");
        CrashRecoverySystem recovery = null!;
        PlayerTradeSystem market = null!;
        EconomyAccount sellerAccount = null!, buyerAccount = null!;
        EntityUid console = default, crate = default, sellerBody = default, buyerBody = default;
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(RecoveryCVars.Scope, scope);
            server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            recovery = e.System<CrashRecoverySystem>(); market = e.System<PlayerTradeSystem>();
        });
        await PoolManager.WaitUntil(server, () => recovery.Ready, 3600);
        await server.WaitAssertion(() =>
        {
            var sellerSlot = prefs.GetPreferences(seller.UserId).SelectedCharacterIndex;
            var buyerSlot = prefs.GetPreferences(buyer.UserId).SelectedCharacterIndex;
            sellerAccount = Complete(tasks, db.ReadEconomyAccountAsync(seller.UserId.UserId, sellerSlot));
            buyerAccount = Complete(tasks, db.ReadEconomyAccountAsync(buyer.UserId.UserId, buyerSlot));
            sellerBody = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            buyerBody = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            // Preview dummies have no life state; seed the same living-actor gate as real players.
            e.EnsureComponent<MobStateComponent>(sellerBody);
            e.EnsureComponent<MobStateComponent>(buyerBody);
            server.PlayerMan.SetAttachedEntity(seller, sellerBody); server.PlayerMan.SetAttachedEntity(buyer, buyerBody);
            recovery.RegisterBody(sellerBody, seller.UserId.UserId, sellerAccount.Profile, sellerSlot);
            recovery.RegisterBody(buyerBody, buyer.UserId.UserId, buyerAccount.Profile, buyerSlot);
            console = e.SpawnEntity("ComputerMarketConsoleNFLow", map.GridCoords);
            var pallet = e.SpawnEntity("CargoPalletSell", map.GridCoords);
            e.System<SharedTransformSystem>().AnchorEntity(console); e.System<SharedTransformSystem>().AnchorEntity(pallet);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, MarketConsoleUiKey.Default, sellerBody);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, MarketConsoleUiKey.Default, buyerBody);
            crate = e.SpawnEntity("CrateGenericSteel", map.GridCoords);
            e.System<MetaDataSystem>().SetEntityName(crate, "Сохраняемый комплект");
            var storage = e.GetComponent<EntityStorageComponent>(crate);
            var disk = e.SpawnEntity("ResearchDisk", map.GridCoords);
            e.GetComponent<ResearchDiskComponent>(disk).Points = 47382;
            Assert.That(e.System<SharedContainerSystem>().Insert(disk, storage.Contents), Is.True);
            var farm = e.SpawnEntity("DatafarmCrypto", map.GridCoords);
            var wallet = e.GetComponent<CryptoFarmComponent>(farm);
            wallet.WalletUser = seller.UserId.UserId; wallet.WalletProfileId = sellerAccount.Profile; wallet.WalletCharacter = "Продавец";
            e.EnsureComponent<EquipmentMaintenanceComponent>(farm).WorkingSeconds = 17321;
            Assert.That(e.System<SharedContainerSystem>().Insert(farm, storage.Contents), Is.True);
            Assert.That(e.System<CargoSystem>().TradingPalletItems(console), Does.Contain(crate));
            Request(market, seller, new() { Console = e.GetNetEntity(console), Action = TradeAction.Refresh });
        });
        await PoolManager.WaitUntil(server, () => Accesses(market).Contains(seller.UserId));
        await Task.Delay(600);
        await server.WaitAssertion(() => Request(market, seller, new() { Console = e.GetNetEntity(console), Action = TradeAction.Sell,
            Token = Token(market, seller.UserId), Item = e.GetNetEntity(crate), Price = 500, Description = "Полный комплект" }));
        TradeBoard board = null!;
        await PoolManager.WaitUntil(server, async () => { board = await db.ReadTradeBoardAsync(seller.UserId.UserId, sellerAccount.Profile); return board.Offers.Length == 1; });
        Assert.That(board.Offers.Single().HasBattery, Is.False, "The saved listing must retain the captured battery metadata.");
        var offer = board.Offers.Single().Id;
        await PoolManager.WaitUntil(server, () => !e.EntityExists(crate));
        await server.WaitAssertion(() =>
        {
            // The seller no longer has an attached body; the database must still credit the correct profile.
            server.PlayerMan.SetAttachedEntity(seller, null);
            Request(market, buyer, new() { Console = e.GetNetEntity(console), Action = TradeAction.Refresh });
        });
        await PoolManager.WaitUntil(server, () => Accesses(market).Contains(buyer.UserId));
        await Task.Delay(600);
        await server.WaitAssertion(() => Request(market, buyer, new() { Console = e.GetNetEntity(console), Action = TradeAction.Cancel,
            Token = Token(market, buyer.UserId), Offer = offer }));
        await PoolManager.WaitUntil(server, () => !e.System<BankSystem>().IsPaymentPending(buyer.UserId));
        await server.WaitAssertion(() =>
        {
            Assert.That(Complete(tasks, db.ReadTradeBoardAsync(seller.UserId.UserId, sellerAccount.Profile)).Offers.Single().Id, Is.EqualTo(offer));
            Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(buyer.UserId.UserId, buyerAccount.Slot)).Bank, Is.EqualTo(buyerAccount.Bank));
        });
        await Task.Delay(600);
        await server.WaitAssertion(() => Request(market, buyer, new() { Console = e.GetNetEntity(console), Action = TradeAction.Refresh }));
        await PoolManager.WaitUntil(server, () => Accesses(market).Contains(buyer.UserId));
        await Task.Delay(600);
        await server.WaitAssertion(() =>
        {
            var bank = e.System<BankSystem>();
            Assert.That(bank.BeginExchangePayment(seller.UserId), Is.True);
            try
            {
                Request(market, buyer, new() { Console = e.GetNetEntity(console), Action = TradeAction.Deal,
                    Token = Token(market, buyer.UserId), Offer = offer });
                Assert.That(bank.IsPaymentPending(buyer.UserId), Is.False, "A busy counterparty must not strand the buyer's bank lock.");
                Assert.That(Complete(tasks, db.ReadTradeBoardAsync(seller.UserId.UserId, sellerAccount.Profile)).Offers.Length, Is.EqualTo(1));
            }
            finally { bank.EndExchangePayment(seller.UserId); }
        });
        await Task.Delay(600);
        await server.WaitAssertion(() => Request(market, buyer, new() { Console = e.GetNetEntity(console), Action = TradeAction.Deal,
            Token = Token(market, buyer.UserId), Offer = offer }));
        await PoolManager.WaitUntil(server, async () => { board = await db.ReadTradeBoardAsync(buyer.UserId.UserId, buyerAccount.Profile); return board.Deliveries.Length == 1; });
        var delivery = board.Deliveries.Single().Id;
        await Task.Delay(600);
        await PoolManager.WaitUntil(server, () => Accesses(market).Contains(buyer.UserId));
        await server.WaitAssertion(() => Request(market, buyer, new() { Console = e.GetNetEntity(console), Action = TradeAction.Claim,
            Token = Token(market, buyer.UserId), Offer = delivery }));
        await PoolManager.WaitUntil(server, () => e.EntityQuery<MetaDataComponent, TransformComponent>().Any(p => p.Item1.EntityName == "Сохраняемый комплект" && p.Item2.GridUid == map.Grid), 1800);
        await server.WaitAssertion(() =>
        {
            var restored = e.EntityQuery<MetaDataComponent, TransformComponent>().Single(p => p.Item1.EntityName == "Сохраняемый комплект" && p.Item2.GridUid == map.Grid).Item1.Owner;
            var contents = e.GetComponent<EntityStorageComponent>(restored).Contents.ContainedEntities;
            var disk = contents.Single(uid => e.HasComponent<ResearchDiskComponent>(uid));
            Assert.That(e.GetComponent<ResearchDiskComponent>(disk).Points, Is.EqualTo(47382));
            var farm = contents.Single(uid => e.HasComponent<CryptoFarmComponent>(uid));
            Assert.That(e.GetComponent<EquipmentMaintenanceComponent>(farm).WorkingSeconds, Is.EqualTo(17321));
            Assert.That(e.GetComponent<CryptoFarmComponent>(farm).IsBound, Is.False, "Transferred farm must not keep paying its former owner.");
            var sellerBank = Complete(tasks, db.ReadEconomyAccountAsync(seller.UserId.UserId, sellerAccount.Slot));
            var buyerBank = Complete(tasks, db.ReadEconomyAccountAsync(buyer.UserId.UserId, buyerAccount.Slot));
            Assert.That(sellerBank.Bank, Is.EqualTo(sellerAccount.Bank + 500));
            Assert.That(buyerBank.Bank, Is.EqualTo(buyerAccount.Bank - 500));
            Assert.That(Complete(tasks, db.ReadTradeBoardAsync(buyer.UserId.UserId, buyerAccount.Profile)).Deliveries, Is.Empty);
        });
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }
}
