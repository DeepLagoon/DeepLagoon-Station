using System.Linq;
using System.Reflection;
using Content.Client._DeepLagoon.Apartments;
using Content.Client._DeepLagoon.Currency;
using Content.Shared._DeepLagoon.Currency;
using Robust.Client.UserInterface;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LagoonEconomyWindowTests
{
    [Test]
    public async Task MenuOpensBeforeServerReplyAndIgnoresRepliesToClosedWindows()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var system = pair.Client.EntMan.System<LagoonEconomySystem>();
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            Guid Request() => (Guid) typeof(LagoonEconomySystem).GetField("_request", flags)!.GetValue(system)!;
            void Reply(Guid request) => typeof(LagoonEconomySystem).GetMethod("OnState", flags)!.Invoke(system,
                new object[] { new LagoonEconomyStateEvent(Guid.Empty, "{\"error\":\"Test unavailable\"}", request) });

            system.Open();
            var first = ui.WindowRoot.Children.OfType<ApartmentTravelWindow>().Single();
            Assert.That(first.IsOpen, Is.True, "Clicking the menu must open a window before the server has replied");
            var oldRequest = Request();
            system.Open();
            Assert.That(ui.WindowRoot.Children.OfType<ApartmentTravelWindow>().Count(), Is.EqualTo(1));
            first.Close();
            Reply(oldRequest);
            Assert.That(ui.WindowRoot.Children.OfType<ApartmentTravelWindow>().Any(), Is.False,
                "A delayed reply must not reopen a closed menu");

            system.Open();
            var second = ui.WindowRoot.Children.OfType<ApartmentTravelWindow>().Single();
            var currentRequest = Request();
            Assert.That(currentRequest, Is.Not.EqualTo(oldRequest));
            var before = typeof(Content.Client._DeepLagoon.WebUI.TguiPanel).GetField("_data", flags)!.GetValue(second.Panel);
            Reply(oldRequest);
            Assert.That(typeof(Content.Client._DeepLagoon.WebUI.TguiPanel).GetField("_data", flags)!.GetValue(second.Panel), Is.EqualTo(before),
                "An old reply must not overwrite a newly opened menu");
            Reply(currentRequest);
            Assert.That(typeof(Content.Client._DeepLagoon.WebUI.TguiPanel).GetField("_data", flags)!.GetValue(second.Panel), Does.Contain("Test unavailable"));
            second.Close();
        });
        await pair.CleanReturnAsync();
    }
}
