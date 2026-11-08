using Content.Server._DeepLagoon.Currency;
using Content.Server._NF.Bank;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Shared._NF.Bank.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Preferences;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ExpeditionSponsorshipTests
{
    [Test]
    public async Task OnlyContractorsReceivePersistentHourlyDeposits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Destructive = true,
            DummyTicker = false,
        });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var session = pair.Player!;
        var ticker = server.System<GameTicker>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => ticker.UserHasJoinedGame(session) &&
            preferences.TryGetCachedPreferences(session.UserId, out _), 600);

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var sponsorship = server.System<ExpeditionSponsorshipSystem>();
            var bank = server.System<BankSystem>();
            var database = server.ResolveDependency<IServerDbManager>();
            var tasks = server.ResolveDependency<ITaskManager>();
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).SelectedCharacter;
            var body = entities.SpawnEntity("MobHuman", map.GridCoords);
            var minds = server.System<SharedMindSystem>();
            minds.TransferTo(minds.CreateMind(session.UserId), body);
            bank.SetConfirmedBankBalance(body, profile.BankBalance);
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(session.AttachedEntity, Is.EqualTo(body));

            void SpawnAs(string job) => entities.EventBus.RaiseEvent(EventSource.Local,
                new PlayerSpawnCompleteEvent(body, session, job, true, false, 1, map.MapUid, profile));

            void CheckBalance(int expected)
            {
                Assert.That(entities.GetComponent<BankAccountComponent>(body).Balance, Is.EqualTo(expected));
                var persisted = database.GetPlayerPreferencesAsync(session.UserId, default);
                tasks.BlockWaitOnTask(persisted);
                var saved = (HumanoidCharacterProfile) persisted.GetAwaiter().GetResult()!.SelectedCharacter;
                Assert.That(saved.BankBalance, Is.EqualTo(expected));
            }

            SpawnAs("Passenger");
            sponsorship.Update(3600);
            CheckBalance(profile.BankBalance);

            SpawnAs("Contractor");
            sponsorship.Update(3599);
            CheckBalance(profile.BankBalance);
            SpawnAs("Contractor");
            sponsorship.Update(1);
            CheckBalance(profile.BankBalance + 5000);
            sponsorship.Update(3600);
            CheckBalance(profile.BankBalance + 10000);
        });

        await pair.CleanReturnAsync();
    }
}
