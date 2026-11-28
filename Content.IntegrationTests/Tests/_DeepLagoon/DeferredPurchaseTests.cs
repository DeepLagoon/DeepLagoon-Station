using System;
using System.Linq;
using Content.Server._DeepLagoon.Economy;
using Content.Server._NF.Bank;
using Content.Server.Cargo.Components;
using Content.Server.Power.Components;
using Content.Shared._Mono.CCVar;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Access.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Events;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DeferredPurchaseTests
{
    [Test]
    public async Task AChangedCargoPriceAutomaticallyBuysOnceAndRechecksFundsAndConsoleUseAfterTheDelay()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap(); var server = pair.Server; var e = server.EntMan;
        EntityUid console = default, actor = default;
        StationCargoOrderDatabaseComponent orders = default!;
        CargoOrderData order = default!;
        var session = server.PlayerMan.Sessions.Single();
        long balance = 0; int finalPrice = 0;
        void Approve() => e.EventBus.RaiseLocalEvent(console, new CargoConsoleApproveOrderMessage(order.OrderId) { Actor = actor });
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            server.CfgMan.SetCVar(MonoCVars.DepositEnabled, true);
            var station = e.SpawnEntity(null, map.GridCoords);
            orders = e.AddComponent<StationCargoOrderDatabaseComponent>(station);
            e.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
            console = e.SpawnEntity("ComputerCargoOrders", map.GridCoords);
            e.RemoveComponent<AccessReaderComponent>(console);
            e.RemoveComponent<ApcPowerReceiverComponent>(console);
            actor = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            e.EnsureComponent<BankAccountComponent>(actor);
            server.PlayerMan.SetAttachedEntity(session, actor);
            Assert.That(e.System<BankSystem>().TryBankDeposit(actor, 100000), Is.True);
            balance = e.GetComponent<BankAccountComponent>(actor).Balance;
            e.System<SharedUserInterfaceSystem>().OpenUi(console, CargoConsoleUiKey.Orders, actor);
            order = new(1, "CrateGenericSteel", "test", 1, 2, "test", "", e.GetNetEntity(console)) { BasePrice = 1000 };
            orders.Orders.Add(order);
            Approve(); Approve();
            Assert.That(order.Approved, Is.False);
            Assert.That(e.System<DeferredPurchaseSystem>().IsPending(console, actor, "cargo:1"), Is.True);
            Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(balance));
        });
        await pair.RunTicksSync(15);
        await server.WaitAssertion(() =>
        {
            Assert.That(order.Approved, Is.False, "The price refresh must cause a real delay.");
            var economy = e.System<SectorEconomySystem>();
            economy.Purchased("cargo:CrateGenericSteel", 3);
            finalPrice = (int)economy.Quote("cargo:CrateGenericSteel", 1000,
                server.ResolveDependency<IPrototypeManager>().Index<EntityPrototype>("CrateGenericSteel"));
        });
        await pair.RunTicksSync(25);
        await server.WaitAssertion(() =>
        {
            Assert.That(order.Approved, Is.True, "No second click, even if the price changes again while waiting.");
            Assert.That(order.Price, Is.EqualTo(finalPrice));
            Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(balance - 2 * finalPrice));
            Assert.That(e.System<DeferredPurchaseSystem>().IsPending(console, actor, "cargo:1"), Is.False);
            Approve();
            Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(balance - 2 * finalPrice));
        });
        foreach (var cancel in new[] { "funds", "close", "body" })
        {
            await server.WaitAssertion(() =>
            {
                server.PlayerMan.SetAttachedEntity(session, actor);
                e.System<SharedUserInterfaceSystem>().OpenUi(console, CargoConsoleUiKey.Orders, actor);
                order = new(orders.Orders.Count + 1, "CrateGenericSteel", cancel, 1, 1, "test", "", e.GetNetEntity(console)) { BasePrice = 1000 };
                orders.Orders.Add(order); Approve();
                Assert.That(e.System<DeferredPurchaseSystem>().IsPending(console, actor, "cargo:" + order.OrderId), Is.True);
                if (cancel == "funds")
                {
                    var money = e.GetComponent<BankAccountComponent>(actor).Balance;
                    Assert.That(e.System<BankSystem>().TryBankWithdraw(actor, money - 1), Is.True);
                }
                else if (cancel == "close") e.System<SharedUserInterfaceSystem>().CloseUi(console, CargoConsoleUiKey.Orders, actor);
                else server.PlayerMan.SetAttachedEntity(session, null);
                balance = e.GetComponent<BankAccountComponent>(actor).Balance;
            });
            await pair.RunTicksSync(40);
            await server.WaitAssertion(() =>
            {
                Assert.That(order.Approved, Is.False, cancel);
                Assert.That(e.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(balance), cancel);
                Assert.That(e.System<DeferredPurchaseSystem>().IsPending(console, actor, "cargo:" + order.OrderId), Is.False);
                // Later cases must reach the deferred guard rather than start out unaffordable.
                server.PlayerMan.SetAttachedEntity(session, actor);
                Assert.That(e.System<BankSystem>().TryBankDeposit(actor, 10000), Is.True);
            });
        }
        await pair.CleanReturnAsync();
    }
}
