using Content.Client._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.CCVar;
using Robust.Client.Console;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests.Networking
{
    [TestFixture]
    public sealed class ReconnectTest
    {
        [Test]
        public async Task Test()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var client = pair.Client;

            var host = client.ResolveDependency<IClientConsoleHost>();
            var netManager = client.ResolveDependency<IClientNetManager>();
            var overlays = client.ResolveDependency<IOverlayManager>();
            var cfg = client.ResolveDependency<IConfigurationManager>();
            StarLightOverlay previousLight = null;
            var renderStars = false;

            await client.WaitAssertion(() =>
            {
                renderStars = cfg.GetCVar(FHCCVars.RenderStarSystem);
                cfg.SetCVar(FHCCVars.RenderStarSystem, true);
                previousLight = overlays.GetOverlay<StarLightOverlay>();
            });

            await client.WaitPost(() => host.ExecuteCommand("disconnect"));

            // Run some ticks for the disconnect to complete and such.
            await pair.RunTicksSync(5);

            await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());

            await client.WaitAssertion(() =>
            {
                // A disconnected system must not retain overlays or its CVar callback.
                cfg.SetCVar(FHCCVars.RenderStarSystem, false);
                cfg.SetCVar(FHCCVars.RenderStarSystem, true);
                Assert.Multiple(() =>
                {
                    Assert.That(overlays.HasOverlay<StarOverlay>(), Is.False);
                    Assert.That(overlays.HasOverlay<PlanetOverlay>(), Is.False);
                    Assert.That(overlays.HasOverlay<AsteroidBeltOverlay>(), Is.False);
                    Assert.That(overlays.HasOverlay<StarLightOverlay>(), Is.False);
                });
            });

            // Reconnect.
            client.SetConnectTarget(server);

            await client.WaitPost(() => netManager.ClientConnect(null, 0, null));

            // Run some ticks for the handshake to complete and such.
            await pair.RunTicksSync(10);

            await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());
            await client.WaitAssertion(() =>
            {
                Assert.That(overlays.GetOverlay<StarLightOverlay>(), Is.Not.SameAs(previousLight));
                cfg.SetCVar(FHCCVars.RenderStarSystem, renderStars);
            });
            await pair.CleanReturnAsync();
        }
    }
}
