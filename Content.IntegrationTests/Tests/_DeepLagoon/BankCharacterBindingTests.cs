using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._NF.Bank;
using Content.Server._NF.CryoSleep;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server.Cargo.Components;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Server.Preferences.Managers;
using Content.Server.VendingMachines;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Access.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Events;
using Content.Shared.CCVar;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Preferences;
using Content.Shared.Players;
using Content.Shared.Station.Components;
using Content.Shared.VendingMachines;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using static Content.Shared._NF.CryoSleep.SharedCryoSleepSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BankCharacterBindingTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: DLBankIdentityProduct
  components:
  - type: StaticPrice
    price: 1000
- type: vendingMachineInventory
  id: DLBankIdentityInventory
  startingInventory:
    DLBankIdentityProduct: 4294967295
- type: entity
  id: DLBankIdentityVendor
  components:
  - type: VendingMachine
    pack: DLBankIdentityInventory
    requiresCash: true
  - type: Appearance
  - type: CreditReceiver
  - type: ItemSlots
  - type: ContainerContainer
    containers:
      cash_slot: !type:ContainerSlot
";

    [TestCase("vending")]
    [TestCase("cargo")]
    public async Task CryosleepObserveAndDifferentLobbyCharacterCannotRedirectMoneyOrFundUnlimitedPurchases(string purchase)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server; var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var e = server.EntMan; var session = server.PlayerMan.Sessions.Single();
            var tasks = server.ResolveDependency<ITaskManager>();
            var prefs = server.ResolveDependency<IServerPreferencesManager>();
            var database = server.ResolveDependency<IServerDbManager>();
            void Complete(Task task) { tasks.BlockWaitOnTask(task); task.GetAwaiter().GetResult(); }
            var a = prefs.GetPreferences(session.UserId).SelectedCharacterIndex;
            var b = a == 0 ? 1 : 0;
            var original = (HumanoidCharacterProfile)prefs.GetPreferences(session.UserId).SelectedCharacter;
            void Seed(int slot, string name, long amount)
            {
                Complete(prefs.SetProfile(session.UserId, slot, original.WithName(name).WithBankBalance(amount)));
                var stored = database.GetPlayerPreferencesAsync(session.UserId, default); tasks.BlockWaitOnTask(stored);
                var balance = ((HumanoidCharacterProfile)stored.GetAwaiter().GetResult()!.Characters[slot]).BankBalance;
                if (balance > amount) Complete(database.WithdrawBankAsync(session.UserId, slot, balance, balance - amount));
                if (balance < amount) Complete(database.DepositBankAsync(session.UserId, slot, balance, amount - balance));
            }
            Seed(a, "Dwarf A", 20000); Seed(b, "Observer B", 74000);
            void Select(int slot) => prefs.GetType().GetMethod("HandleSelectCharacterMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(prefs, [new MsgSelectCharacter { SelectedCharacterIndex = slot, MsgChannel = session.Channel }]);
            Select(a);
            var actor = e.SpawnEntity("MobHuman", map.GridCoords);
            e.EnsureComponent<BankAccountComponent>(actor);
            var minds = e.System<MindSystem>(); var mind = minds.GetOrCreateMind(session.UserId);
            minds.TransferTo(mind.Owner, actor);
            var bank = e.System<BankSystem>();
            Assert.That(bank.TryGetBalance(actor, out var initial), Is.True);
            Assert.That(initial, Is.EqualTo(20000));
            var pod = e.SpawnEntity("MachineCryoSleepPod", map.GridCoords);
            var cryo = e.System<CryoSleepSystem>();
            Assert.That(cryo.InsertBody(actor, e.GetComponent<CryoSleepComponent>(pod), false), Is.True);
            cryo.AcceptCryoSleep(actor, pod);
            Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.True);
            Assert.That(e.HasComponent<GhostComponent>(session.AttachedEntity), Is.True);
            Select(b);
            // The same path as observe, with a fresh observer mind named after the selected profile.
            var ticker = e.System<GameTicker>(); var dummy = ticker.DummyTicker; server.CfgMan.SetCVar(CCVars.GameDummyTicker, false);
            try { ticker.JoinAsObserver(session); }
            finally { server.CfgMan.SetCVar(CCVars.GameDummyTicker, dummy); }
            var observer = session.GetMind()!.Value;
            Assert.That(cryo.TryReturnToBody(e.GetComponent<MindComponent>(observer)), Is.EqualTo(ReturnToBodyStatus.Success));
            Assert.That(session.AttachedEntity, Is.EqualTo(actor));
            Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(20000), "The returned dwarf must not inherit B's 74000 account.");
            Assert.That(bank.TryGetBalance(session, out var restored), Is.True); Assert.That(restored, Is.EqualTo(20000));
            // Updating A's cached balance must not change the separately selected lobby profile B.
            var selected = prefs.GetPreferences(session.UserId).SelectedCharacterIndex;
            Complete(prefs.SetProfile(session.UserId, a, ((HumanoidCharacterProfile)prefs.GetPreferences(session.UserId).Characters[a]).WithBankBalance(20000), persist: false));
            Assert.That(prefs.GetPreferences(session.UserId).SelectedCharacterIndex, Is.EqualTo(selected));
            Assert.That(bank.TryBankDeposit(actor, 500), Is.True);
            Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(20500));
            var station = e.SpawnEntity(null, map.GridCoords);
            e.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
            var orders = e.EnsureComponent<StationCargoOrderDatabaseComponent>(station);
            var terminal = e.SpawnEntity("ComputerCargoOrders", map.GridCoords); e.RemoveComponent<AccessReaderComponent>(terminal);
            var vendor = e.SpawnEntity("DLBankIdentityVendor", map.GridCoords);
            var vending = e.System<VendingMachineSystem>(); var vendingComponent = e.GetComponent<VendingMachineComponent>(vendor);
            var spent = 0L; var accepted = 0;
            for (var i = 0; i < 100; i++)
            {
                var before = e.GetComponent<BankAccountComponent>(actor).Balance;
                if (purchase == "vending")
                {
                    var price = vendingComponent.Inventory["DLBankIdentityProduct"].Price!.Value;
                    vending.AuthorizedVend(vendor, actor, InventoryType.Regular, "DLBankIdentityProduct", vendingComponent);
                    if (vendingComponent.Ejecting) { accepted++; spent += price; }
                    vending.Update(vendingComponent.EjectDelay + .1f);
                }
                else
                {
                    var order = new CargoOrderData(i + 1, "CrateGenericSteel", "purchase", 1000, 1, "test", "", e.GetNetEntity(terminal));
                    orders.Orders.Add(order);
                    e.EventBus.RaiseLocalEvent(terminal, new CargoConsoleApproveOrderMessage(order.OrderId) { Actor = actor });
                    if (order.Approved) { accepted++; spent += order.Price; }
                    orders.Orders.Remove(order);
                }
                Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(20500 - spent));
                Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.LessThanOrEqualTo(before));
            }
            Assert.That(accepted, Is.InRange(1, 20));
            // A stale component must not pass the payment gate and create free goods.
            var purchases = vendingComponent.TotalPurchases;
            var delivered = e.EntityQuery<MetaDataComponent>().Count(m => m.EntityPrototype?.ID == "DLBankIdentityProduct");
            for (var i = 0; i < 10; i++)
            {
                bank.SetConfirmedBankBalance(actor, 74000);
                vending.AuthorizedVend(vendor, actor, InventoryType.Regular, "DLBankIdentityProduct", vendingComponent);
                vending.Update(vendingComponent.EjectDelay + .1f);
                Assert.That(vendingComponent.TotalPurchases, Is.EqualTo(purchases));
                Assert.That(e.EntityQuery<MetaDataComponent>().Count(m => m.EntityPrototype?.ID == "DLBankIdentityProduct"), Is.EqualTo(delivered));
            }
            // A pending exchange transaction also has to cancel dispensing, even with enough money.
            Assert.That(bank.TryBankDeposit(actor, 5000), Is.True);
            Assert.That(bank.BeginExchangePayment(session.UserId), Is.True);
            try
            {
                vending.AuthorizedVend(vendor, actor, InventoryType.Regular, "DLBankIdentityProduct", vendingComponent);
                vending.Update(vendingComponent.EjectDelay + .1f);
                Assert.That(vendingComponent.TotalPurchases, Is.EqualTo(purchases));
                Assert.That(e.EntityQuery<MetaDataComponent>().Count(m => m.EntityPrototype?.ID == "DLBankIdentityProduct"), Is.EqualTo(delivered));
            }
            finally { bank.EndExchangePayment(session.UserId); }
            var saved = database.GetPlayerPreferencesAsync(session.UserId, default); tasks.BlockWaitOnTask(saved);
            var stored = saved.GetAwaiter().GetResult()!;
            Assert.That(((HumanoidCharacterProfile)stored.Characters[a]).BankBalance, Is.EqualTo(25500 - spent));
            Assert.That(((HumanoidCharacterProfile)stored.Characters[b]).BankBalance, Is.EqualTo(74000));
            TestContext.Progress.WriteLine($"{purchase}: accepted {accepted}/100, spent {spent}, A after purchases {20500 - spent}, B unchanged 74000; stale-balance and pending-payment attempts delivered no goods.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MindTransferAndBankTransferUseBothBodiesCharactersDespiteDifferentLobbySelections()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server; var map = await pair.CreateTestMap();
        var recipient = await server.AddDummySession("bank_identity_recipient");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(recipient.UserId, out _));
        await server.WaitAssertion(() =>
        {
            var e = server.EntMan; var tasks = server.ResolveDependency<ITaskManager>();
            var sender = server.PlayerMan.Sessions.Single(s => s != recipient);
            var bank = e.System<BankSystem>(); var minds = e.System<MindSystem>();
            var a = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<BankAccountComponent>(a);
            var b = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<BankAccountComponent>(b);
            var senderMind = minds.GetOrCreateMind(sender.UserId); var recipientMind = minds.GetOrCreateMind(recipient.UserId);
            minds.TransferTo(senderMind.Owner, a); minds.TransferTo(recipientMind.Owner, b);
            Assert.That(bank.TryGetBalance(a, out var beforeA), Is.True);
            Assert.That(bank.TryGetBalance(b, out var beforeB), Is.True);
            foreach (var session in new[] { sender, recipient })
            {
                var selected = prefs.GetPreferences(session.UserId);
                var slot = selected.SelectedCharacterIndex == 0 ? 1 : 0;
                var save = prefs.SetProfile(session.UserId, slot, ((HumanoidCharacterProfile)selected.SelectedCharacter).WithName("Other character").WithBankBalance(74000));
                tasks.BlockWaitOnTask(save); save.GetAwaiter().GetResult();
                prefs.GetType().GetMethod("HandleSelectCharacterMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(prefs, [new MsgSelectCharacter { SelectedCharacterIndex = slot, MsgChannel = session.Channel }]);
            }
            Assert.That(bank.TryTransfer(sender, recipient, 100, out var result), Is.True);
            Assert.That(result.SenderBalance, Is.EqualTo(beforeA - 100));
            Assert.That(result.RecipientBalance, Is.EqualTo(beforeB + 100));
            Assert.That(((HumanoidCharacterProfile)prefs.GetPreferences(sender.UserId).SelectedCharacter).BankBalance, Is.EqualTo(74000));
            Assert.That(((HumanoidCharacterProfile)prefs.GetPreferences(recipient.UserId).SelectedCharacter).BankBalance, Is.EqualTo(74000));
            var clone = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<BankAccountComponent>(clone);
            minds.TransferTo(senderMind.Owner, clone);
            Assert.That(e.GetComponent<BankAccountComponent>(clone).Balance, Is.EqualTo(beforeA - 100));
            Assert.That(bank.TryBankWithdraw(clone, 1), Is.True);
            Assert.That(bank.TryGetBalance(sender, out var after), Is.True);
            Assert.That(after, Is.EqualTo(beforeA - 101));
            Assert.That(e.GetComponent<BankCharacterComponent>(clone).Profile, Is.EqualTo(e.GetComponent<BankCharacterComponent>(a).Profile));
            // A restored body's original identity takes precedence over the incoming mind's previous character.
            var otherBody = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<BankAccountComponent>(otherBody);
            bank.PreparePlayerSpawn(otherBody, sender, prefs.GetPreferences(sender.UserId).SelectedCharacter);
            minds.TransferTo(senderMind.Owner, otherBody);
            Assert.That(e.GetComponent<BankAccountComponent>(otherBody).Balance, Is.EqualTo(74000));
            var restoredBody = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<BankAccountComponent>(restoredBody);
            var recovered = e.EnsureComponent<RecoveryBodyComponent>(restoredBody);
            var original = e.GetComponent<BankCharacterComponent>(a);
            recovered.User = original.User; recovered.Slot = original.Slot; recovered.Profile = original.Profile;
            minds.TransferTo(senderMind.Owner, restoredBody);
            Assert.That(e.GetComponent<BankAccountComponent>(restoredBody).Balance, Is.EqualTo(beforeA - 101));
        });
        await pair.CleanReturnAsync();
    }
}
