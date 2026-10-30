using System.Linq;
using System.Reflection;
using Content.Client.Audio.Jukebox;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Audio.Jukebox;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Client.WebView;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class JukeboxClientTests
{
    [TestCase("AdminObserver", false)]
    [TestCase("AdminObserver", true)]
    [TestCase("MobHuman", false)]
    [TestCase("MobHuman", true)]
    public async Task TguiClosesWithoutDisposingBrowserDuringPendingCefCallbacks(string prototype, bool fallback)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server.EntMan;
        EntityUid jukebox = default;
        await pair.Client.WaitPost(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "Jukebox=1");
        });
        await pair.Server.WaitAssertion(() =>
        {
            var actor = server.SpawnEntity(prototype, map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
            jukebox = server.SpawnEntity("Jukebox", map.GridCoords);
            server.System<Robust.Server.GameObjects.UserInterfaceSystem>().OpenUi(jukebox, JukeboxUiKey.Key, actor);
        });
        var netJukebox = server.GetNetEntity(jukebox);
        await pair.RunTicksSync(30);
        JukeboxBoundUserInterface bui = null;
        JukeboxTguiWindow window = null;
        WebViewControl browser = null;
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            Assert.That(client.System<Robust.Client.GameObjects.UserInterfaceSystem>()
                .TryGetOpenUi(client.GetEntity(netJukebox), JukeboxUiKey.Key, out bui), Is.True);
            window = (JukeboxTguiWindow) typeof(JukeboxBoundUserInterface)
                .GetField("_tguiWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bui)!;
            Assert.That(window, Is.Not.Null);
            // Headless clients do not draw frames. Start the real host explicitly
            // so this covers the WebView child and its UI-tree close lifecycle.
            typeof(GameWebView).GetMethod("FrameUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window.Panel.Web, [new FrameEventArgs(.1f)]);
            browser = window.Panel.Web.Children.OfType<WebViewControl>().Single();
            Assert.That(browser.IsInsideTree, Is.True);
            if (fallback)
                typeof(JukeboxBoundUserInterface).GetMethod("DeferClassic", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bui, null);
            else
                bui.Close();
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(window.IsInsideTree, Is.False);
            Assert.That(browser.IsInsideTree, Is.False);
            Assert.That(pair.Client.ResolveDependency<WebViewLifetimeRegistry>().Active.ContainsKey(browser), Is.False);
            Assert.That(browser.Disposed, Is.False,
                "CEF can request GetViewRect until OnBeforeClose; disposing makes the signed module return an empty rectangle and abort");
            Assert.That(window.Disposed, Is.False);
            if (fallback) bui.Close();
        });
        await pair.RunTicksSync(10);
        await pair.CleanReturnAsync();
    }
}
