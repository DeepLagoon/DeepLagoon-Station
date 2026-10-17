using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby.UI;
using Content.Shared.CCVar;
using Robust.Client.UserInterface;
using Robust.Client.WebView;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class LobbyBackgroundMediaTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Tick(LobbyBackground background) => typeof(LobbyBackground)
        .GetMethod("FrameUpdate", Private)!.Invoke(background, new object[] { new FrameEventArgs(0.1f) });
    private static WebViewControl View(LobbyBackground background) =>
        (WebViewControl)typeof(LobbyBackground).GetField("_mediaView", Private)!.GetValue(background);

    [Test]
    public async Task LeavingLobbyClosesBackgroundAndReturnCreatesFreshBrowser()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var background = new LobbyBackground();
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var registry = pair.Client.ResolveDependency<WebViewLifetimeRegistry>();
            ui.WindowRoot.AddChild(background);
            background.SetSource("/Web/DeepLagoon/LobbyMedia/test.webm");
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.False, "Attaching a cached screen must not start playback outside LobbyState");
            background.Start();
            Tick(background);
            var first = View(background);
            Assert.That(background.MediaBrowserActive, Is.True);
            Assert.That(first.AlwaysActive || first.CanKeyboardFocus || first.KeyboardFocusOnClick, Is.False);
            Assert.That(first.MouseFilter, Is.EqualTo(Control.MouseFilterMode.Ignore));
            background.Stop();
            Assert.That(first.Parent, Is.Null);
            Assert.That(registry.Active.ContainsKey(first), Is.False);
            for (var i = 0; i < 100; i++) Tick(background);
            Assert.That(background.MediaBrowserActive, Is.False, "Gameplay frames cannot recreate a stopped background");
            background.Start();
            Tick(background);
            var second = View(background);
            Assert.That(second, Is.Not.SameAs(first));
            background.Visible = false;
            Assert.That(second.Parent, Is.Null);
            Assert.That(registry.Active.ContainsKey(second), Is.False);
            background.Visible = true;
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.True);
            background.SetSource(LobbyBackground.DefaultBackground);
            Assert.That(background.MediaBrowserActive, Is.False);
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FailureSettingsAndShutdownCannotLeaveOrResurrectBrowser()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        LobbyBackground background = null;
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var config = pair.Client.ResolveDependency<IConfigurationManager>();
            background = new LobbyBackground();
            ui.WindowRoot.AddChild(background);
            background.Start();
            background.SetSource("/Textures/LobbyScreens/test.gif");
            config.SetCVar(CCVars.LobbyAnimatedBackground, false);
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.False);
            config.SetCVar(CCVars.LobbyAnimatedBackground, true);
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.True);
            pair.Client.ResolveDependency<ILogManager>().GetSawmill("lobby.background").Level = LogLevel.Error;
            typeof(LobbyBackground).GetField("_status", Private)!.SetValue(background, 2);
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.False);
            Assert.That(background.Texture, Is.Not.Null, "Decoder failure retains the static background");
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.False, "A broken file must not be retried every frame");
            background.Start();
            Tick(background);
            background.Orphan();
        });
        await pair.Client.WaitRunTicks(2);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(View(background), Is.Null, "Removing the UI tree also releases the managed browser owner");
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            ui.WindowRoot.AddChild(background);
            background.Start();
            Tick(background);
            var view = View(background);
            WebViewLifetime.Shutdown();
            Assert.That(view.Parent, Is.Null);
            background.Start();
            Tick(background);
            Assert.That(background.MediaBrowserActive, Is.False, "CEF shutdown must never be followed by a new browser");
            background.Dispose();
        });
        await pair.CleanReturnAsync();
    }
}
