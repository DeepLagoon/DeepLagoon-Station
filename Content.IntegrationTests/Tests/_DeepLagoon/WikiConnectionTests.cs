using Content.Client.Lobby;
using Robust.Client;
using Robust.Client.State;
using Robust.Client.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class WikiConnectionTests
{
    [Test]
    public async Task LobbyConnectionLocksSubscriptionsAndKeepsApplyingServerStates()
    {
        // A compiler/sandbox check does not exercise EntityEventBus.LockSubscriptions.
        // Different ordering on GuidebookSystem's two ActivateInWorldEvent handlers
        // used to leave BaseClient Connected, simulation paused and the main menu visible.
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.RunTicksSync(5);

        uint firstTick = 0;
        await pair.Client.WaitAssertion(() =>
        {
            var timing = pair.Client.ResolveDependency<IClientGameTiming>();
            Assert.That(pair.Client.EntMan.Started, Is.True);
            Assert.That(pair.Client.ResolveDependency<IBaseClient>().RunLevel,
                Is.AnyOf(ClientRunLevel.Connected, ClientRunLevel.InGame));
            Assert.That(pair.Client.ResolveDependency<IStateManager>().CurrentState, Is.TypeOf<LobbyState>());
            Assert.That(timing.Paused, Is.False);
            firstTick = timing.LastRealTick.Value;
            Assert.That(firstTick, Is.GreaterThan(0));
        });

        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() => Assert.That(
            pair.Client.ResolveDependency<IClientGameTiming>().LastRealTick.Value, Is.GreaterThan(firstTick)));
        await pair.CleanReturnAsync();
    }
}
