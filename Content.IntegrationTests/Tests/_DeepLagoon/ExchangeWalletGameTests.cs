using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server._DeepLagoon.Exchange;
using Content.Server._NF.Bank;
using Content.Server.CartridgeLoader;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.Exchange;
using Content.Shared._NF.Bank.Components;
using Content.Shared.CartridgeLoader;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.PDA;
using Content.Shared.Stacks;
using Robust.Shared.Asynchronous;
using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExchangeWalletGameTests
{
    private static T Complete<T>(ITaskManager tasks, Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }

    [Test]
    public async Task PasswordGateForeignLoginAndCashSalesUseTheSelectedWalletAndDeliverExactCash()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var user = pair.Player!;
        var other = await server.AddDummySession("wallet_owner");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(other.UserId, out _));
        var db = server.ResolveDependency<IServerDbManager>();
        var tasks = server.ResolveDependency<ITaskManager>();
        var exchange = e.System<ExchangeSystem>();
        IDictionary Field(string name) => (IDictionary)typeof(ExchangeSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(exchange)!;
        bool Busy() => ((System.Collections.Generic.HashSet<Guid>)typeof(ExchangeSystem).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(exchange)!).Count > 0;
        int slot = 0;
        EntityUid actor = default, pda = default;
        ExchangeResult own = null!, foreign = null!;
        void Request(ExchangeAction action, Guid quote = default, string address = "", string password = "", string newPassword = "")
        {
            Field("_nextRequest").Clear(); Field("_nextMutation").Clear(); Field("_loginAllowed").Clear();
            typeof(ExchangeSystem).GetMethod("OnRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(exchange,
                [new ExchangeRequest(e.GetNetEntity(pda), action, "contrib", 1000, quote, recipient: address, password: password, newPassword: newPassword), new EntitySessionEventArgs(user)]);
        }
        Guid Quote()
        {
            var quote = Field("_quotes")[user.UserId.UserId]!;
            return (Guid)quote.GetType().GetProperty("Id")!.GetValue(quote)!;
        }
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(ExchangeCVars.Enabled, true);
            server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            server.CfgMan.SetCVar(RecoveryCVars.Scope, "wallet" + Guid.NewGuid().ToString("N"));
        });
        await PoolManager.WaitUntil(server, () => e.System<CrashRecoverySystem>().Ready, 3600);
        await server.WaitAssertion(() =>
        {
            slot = prefs.GetPreferences(user.UserId).SelectedCharacterIndex;
            own = Complete(tasks, db.ReadExchangeAsync(user.UserId, slot, 0));
            foreign = Complete(tasks, db.ReadExchangeAsync(other.UserId, prefs.GetPreferences(other.UserId).SelectedCharacterIndex, 0));
            Complete(tasks, db.LoginExchangeWalletAsync(other.UserId, foreign.Slot, new(foreign.Address, "foreign-secret", foreign.ProfileId)));
            Complete(tasks, db.TradeExchangeAsync(other.UserId, new(Guid.NewGuid(), foreign.Slot, "contrib", 2000, true,
                long.MaxValue, foreign.ProfileId), 1));
            actor = e.SpawnEntity("MobHuman", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(user, actor);
            e.System<BankSystem>().SetConfirmedBankBalance(actor, own.Bank);
            e.System<CrashRecoverySystem>().RegisterBody(actor, user.UserId.UserId, own.ProfileId, slot);
            pda = e.SpawnEntity("ContractorPDA", new EntityCoordinates(actor, Vector2.Zero));
            var loader = e.System<CartridgeLoaderSystem>();
            Assert.That(loader.TryGetProgram<ExchangeCartridgeComponent>(pda, out var program, installedOnly: true), Is.True);
            loader.ActivateProgram(pda, program!.Value);
            e.System<SharedUserInterfaceSystem>().OpenUi(pda, PdaUiKey.Key, actor);
            Request(ExchangeAction.Refresh);
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() => Request(ExchangeAction.Buy, Quote()));
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() =>
        {
            Assert.That(Complete(tasks, db.ReadExchangeAsync(user.UserId, slot, 2)).Bank, Is.EqualTo(own.Bank));
            Request(ExchangeAction.Login, address: own.Address, password: "own-secret");
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() =>
        {
            Assert.That(Field("_walletSessions").Contains(user.UserId.UserId), Is.True);
            Request(ExchangeAction.Buy, Quote());
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        long bank = 0;
        await server.WaitAssertion(() =>
        {
            bank = Complete(tasks, db.ReadExchangeAsync(user.UserId, slot, 3)).Bank;
            Assert.That(bank, Is.LessThan(own.Bank));
            Request(ExchangeAction.Login, address: foreign.Address, password: "wrong-password");
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() =>
        {
            Assert.That(Field("_walletSessions")[user.UserId.UserId]!.GetType().GetProperty("Address")!.GetValue(Field("_walletSessions")[user.UserId.UserId]), Is.EqualTo(own.Address));
            Request(ExchangeAction.Login, address: foreign.Address, password: "foreign-secret");
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        long firstPayout = 0;
        await server.WaitAssertion(() =>
        {
            e.RemoveComponent<BankAccountComponent>(actor);
            var state = Complete(tasks, db.ReadExchangeAsync(user.UserId, slot, 4, walletAddress: foreign.Address));
            firstPayout = state.Market.Quote("contrib", 1000).SellProceeds;
            Request(ExchangeAction.Sell, Quote());
        });
        await PoolManager.WaitUntil(server, () => !Busy() && Field("_cashSales").Count == 0);
        long secondPayout = 0;
        await server.WaitAssertion(() =>
        {
            var cash = e.System<SharedHandsSystem>().EnumerateHeld(actor).Single();
            Assert.That(e.GetComponent<StackComponent>(cash).Count, Is.EqualTo(firstPayout));
            Assert.That(e.EntityQuery<AudioComponent>().Any(a => a.FileName == "/Audio/Effects/Cargo/ping.ogg"), Is.True);
            var state = Complete(tasks, db.ReadExchangeAsync(user.UserId, slot, 5, walletAddress: foreign.Address));
            Assert.That(state.Bank, Is.EqualTo(bank));
            Assert.That(state.Wallet["contrib"], Is.EqualTo(1000));
            var wrench = e.SpawnEntity("Wrench", map.GridCoords);
            Assert.That(e.System<SharedHandsSystem>().TryPickupAnyHand(actor, wrench), Is.True);
            secondPayout = state.Market.Quote("contrib", 1000).SellProceeds;
            Request(ExchangeAction.Sell, Quote());
        });
        await PoolManager.WaitUntil(server, () => !Busy() && Field("_cashSales").Count == 0);
        await server.WaitAssertion(() =>
        {
            var onFloor = e.EntityQuery<StackComponent, TransformComponent>().Where(p => p.Item1.StackTypeId.Id == "Credit" &&
                p.Item2.GridUid == map.Grid.Owner && p.Item2.ParentUid == map.Grid.Owner).ToArray();
            Assert.That(onFloor, Has.Length.EqualTo(1));
            Assert.That(onFloor[0].Item1.Count, Is.EqualTo(secondPayout));
            Assert.That(Complete(tasks, db.ReadExchangeAsync(user.UserId, slot, 6, walletAddress: foreign.Address)).Wallet["contrib"], Is.Zero);
            Assert.That(Complete(tasks, db.ReadExchangeAsync(user.UserId, slot, 6)).Bank, Is.EqualTo(bank));
            Request(ExchangeAction.ChangePassword, password: "wrong-password", newPassword: "replacement-secret");
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() =>
        {
            Assert.That(Field("_walletSessions").Contains(user.UserId.UserId), Is.True);
            Request(ExchangeAction.ChangePassword, password: "foreign-secret", newPassword: "replacement-secret");
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() =>
        {
            Assert.That(Field("_walletSessions").Contains(user.UserId.UserId), Is.False);
            Request(ExchangeAction.Login, address: foreign.Address, password: "foreign-secret");
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() =>
        {
            Assert.That(Field("_walletSessions").Contains(user.UserId.UserId), Is.False);
            Request(ExchangeAction.Login, address: foreign.Address, password: "replacement-secret");
        });
        await PoolManager.WaitUntil(server, () => !Busy());
        await server.WaitAssertion(() =>
        {
            Assert.That(Field("_walletSessions").Contains(user.UserId.UserId), Is.True);
            server.PlayerMan.SetAttachedEntity(user, null);
        });
        await pair.CleanReturnAsync();
    }
}
