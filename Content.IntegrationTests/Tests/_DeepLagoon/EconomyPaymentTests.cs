using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._NF.Bank;
using Content.Server.Database;
using Content.Server.Power.Components;
using Content.Server.Preferences.Managers;
using Content.Server.VendingMachines;
using Content.Shared.VendingMachines;
using NUnit.Framework;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class EconomyPaymentTests
{
    private static T Complete<T>(ITaskManager tasks, Task<T> task)
    { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }

    [Test]
    public async Task AReservedBankCannotDispenseFreeVendorGoodsAndTheSamePurchaseWorksAfterRelease()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("busy_bank_buyer");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(player.UserId, out _));
        var tasks = server.ResolveDependency<ITaskManager>();
        var db = server.ResolveDependency<IServerDbManager>();
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            var slot = prefs.GetPreferences(player.UserId).SelectedCharacterIndex;
            var account = Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, slot));
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(player, body);
            var bank = e.System<BankSystem>(); bank.SetConfirmedBankBalance(body, account.Bank);
            var vendor = e.SpawnEntity("FlatpackVendPremium", map.GridCoords);
            e.GetComponent<ApcPowerReceiverComponent>(vendor).NeedsPower = false;
            e.GetComponent<ApcPowerReceiverComponent>(vendor).Powered = true;
            var comp = e.GetComponent<VendingMachineComponent>(vendor);
            var vending = e.System<VendingMachineSystem>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var entry = comp.Inventory.Values.First(item => vending.GetPurchasePrice(vendor, prototypes.Index<EntityPrototype>(item.ID), comp) is > 0 and < 50000);
            var price = vending.GetPurchasePrice(vendor, prototypes.Index<EntityPrototype>(entry.ID), comp);
            Assert.That(account.Bank, Is.GreaterThanOrEqualTo(price));
            Assert.That(vending.IsAuthorized(vendor, body, comp), Is.True);
            var stock = entry.Amount;
            Assert.That(bank.BeginExchangePayment(player.UserId), Is.True);
            try
            {
                vending.AuthorizedVend(vendor, body, InventoryType.Regular, entry.ID, comp);
                Assert.That(comp.Ejecting, Is.False);
                Assert.That(comp.NextItemToEject, Is.Null);
                Assert.That(entry.Amount, Is.EqualTo(stock));
                Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, slot)).Bank, Is.EqualTo(account.Bank));
            }
            finally { bank.EndExchangePayment(player.UserId); }
            vending.AuthorizedVend(vendor, body, InventoryType.Regular, entry.ID, comp);
            Assert.That(comp.Ejecting, Is.True);
            Assert.That(entry.Amount, Is.EqualTo(stock == uint.MaxValue ? stock : stock - 1));
            Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, slot)).Bank, Is.EqualTo(account.Bank - price));
        });
        await pair.CleanReturnAsync();
    }
}
