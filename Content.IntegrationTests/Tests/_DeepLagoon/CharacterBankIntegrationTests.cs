using System.Linq;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Server._NF.Bank;
using Content.Shared._Mono.CCVar;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Bank.Events;
using Content.Shared.Preferences;
using Robust.Shared.Asynchronous;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterBankIntegrationTests
{
    [TestCase("ComputerBankATM")]
    [TestCase("ComputerBlackMarketBankATM")]
    public async Task FullCashDepositAndWithdrawalUpdateCharacterAccount(string atmPrototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var tasks = pair.Server.ResolveDependency<ITaskManager>();
            var prefs = pair.Server.ResolveDependency<IServerPreferencesManager>();
            var database = pair.Server.ResolveDependency<IServerDbManager>();
            var sender = pair.Server.PlayerMan.Sessions.Single();
            var senderBalance = ((HumanoidCharacterProfile) prefs.GetPreferences(sender.UserId).SelectedCharacter).BankBalance;
            var actor = entities.SpawnEntity("MobHumanDummy", map.GridCoords);
            entities.EnsureComponent<BankAccountComponent>(actor);
            pair.Server.PlayerMan.SetAttachedEntity(sender, actor);
            var bank = entities.System<BankSystem>();
            pair.Server.CfgMan.SetCVar(MonoCVars.DepositEnabled, true);
            pair.Server.CfgMan.SetCVar(MonoCVars.DepositThreshold, 1f);
            pair.Server.CfgMan.SetCVar(MonoCVars.DepositHighExp, 1f);
            var atm = entities.SpawnEntity(atmPrototype, map.GridCoords);
            var key = atmPrototype == "ComputerBlackMarketBankATM" ? BankATMMenuUiKey.BlackMarket : BankATMMenuUiKey.ATM;
            var component = entities.GetComponent<BankATMComponent>(atm);
            var cash = entities.System<StackSystem>().Spawn(100, component.CashType, map.GridCoords);
            Assert.That(entities.System<SharedContainerSystem>().Insert(cash, component.CashSlot.ContainerSlot!), Is.True);

            entities.EventBus.RaiseLocalEvent(atm, new BankDepositMessage { Actor = actor, UiKey = key });
            Assert.That(entities.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(senderBalance + 100));
            Assert.That(component.CashSlot.ContainerSlot.ContainedEntity, Is.Null);
            entities.EventBus.RaiseLocalEvent(atm, new BankWithdrawMessage(40) { Actor = actor, UiKey = key });
            Assert.That(entities.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(senderBalance + 60));

            Assert.That(bank.TryTransfer(sender, sender, 1, out _), Is.False);
            Assert.That(bank.TryBankWithdraw(actor, int.MaxValue), Is.False);
            entities.EnsureComponent<IronmanComponent>(actor);
            Assert.That(bank.TryBankWithdraw(actor, 1), Is.False);
            var saved = database.GetPlayerPreferencesAsync(sender.UserId, default);
            tasks.BlockWaitOnTask(saved);
            Assert.That(((HumanoidCharacterProfile) saved.GetAwaiter().GetResult()!.SelectedCharacter).BankBalance, Is.EqualTo(senderBalance + 60));

            pair.Server.PlayerMan.SetAttachedEntity(sender, null);
            entities.DeleteEntity(actor);
            entities.DeleteEntity(atm);
        });
        await pair.CleanReturnAsync();
    }
}
