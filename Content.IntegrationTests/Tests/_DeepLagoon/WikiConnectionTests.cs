using Content.Client.Lobby;
using Robust.Client;
using Robust.Client.State;
using Robust.Client.Timing;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class WikiConnectionTests
{
    [Test]
    public async Task EveryRoundStartSpeciesHasItsOwnArticleByDefault()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var routes = pair.Client.ResolveDependency<WikiRoutingConfiguration>().Read();
            var defaults = prototypes.Index<WikiPagePrototype>("InGame").SourceRoutes;
            foreach (var species in prototypes.EnumeratePrototypes<SpeciesPrototype>())
            {
                if (!species.RoundStart) continue;
                var source = "species:" + species.ID;
                Assert.That(defaults.ContainsKey(source), Is.True, source);
                Assert.That(routes.Resolve(new[] { source }), Is.EqualTo(defaults[source]), source);
                Assert.That(defaults[source], Does.StartWith("/ru/guidebook/in_game/"), source);
                Assert.That(defaults[source], Is.Not.EqualTo("/ru/in_game"), source);
            }
        });
        await pair.CleanReturnAsync();
    }

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
