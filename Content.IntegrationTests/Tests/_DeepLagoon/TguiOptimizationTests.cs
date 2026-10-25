using System.Reflection;
using System.Collections.Concurrent;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Systems.Chat.Widgets;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TguiOptimizationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task PresentationCoverRejectsStaleAndHiddenAcknowledgements()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var web = new GameWebView(gatePresentation: true);
            pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot.AddChild(web);
            var cover = (Control) typeof(GameWebView).GetField("_presentationCover", Private)!.GetValue(web)!;
            var incoming = (ConcurrentQueue<string>) typeof(GameWebView).GetField("_incoming", Private)!.GetValue(web)!;
            var dispatch = typeof(GameWebView).GetMethod("DispatchIncoming", Private)!;
            void Acknowledge(int presentation)
            {
                incoming.Enqueue("health%2Fpresented&" + Uri.EscapeDataString(new TguiData().Number("presentation", presentation).ToString()));
                dispatch.Invoke(web, null);
            }
            // Headless does not paint CEF: exercise the content-side presentation protocol.
            Assert.That(cover.Visible, Is.True);
            Acknowledge(web.Presentation);
            Assert.That(cover.Visible, Is.True, "A document that is not ready cannot reveal its texture");
            typeof(GameWebView).GetField("_ready", Private)!.SetValue(web, true);
            var old = web.Presentation;
            web.InvalidatePresentation();
            Acknowledge(old);
            Assert.That(cover.Visible, Is.True);
            web.Visible = false;
            Acknowledge(web.Presentation);
            Assert.That(cover.Visible, Is.True);
            web.Visible = true;
            Acknowledge(web.Presentation);
            Assert.That(cover.Visible, Is.False);
            web.InvalidatePresentation();
            Assert.That(cover.Visible, Is.True, "Reopening must cover the previous frame again");
            web.Orphan();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ComposerReusesWindowRejectsStaleDraftsAndDisposesOnNativeFallback()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        ChatBox chat = null;
        BaseWindow window = null;
        var open = typeof(ChatBox).GetMethod("OpenComposer", Private)!;
        var close = typeof(ChatBox).GetMethod("CloseComposer", Private)!;
        var action = typeof(ChatBox).GetMethod("HandleComposerAction", Private)!;
        var retained = typeof(ChatBox).GetField("_composerWindow", Private)!;
        var session = typeof(ChatBox).GetField("_composerSession", Private)!;
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "Chat=1");
            chat = new ChatBox();
            pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot.AddChild(chat);
            open.Invoke(chat, null);
            window = (BaseWindow) retained.GetValue(chat)!;
        });
        await pair.Client.WaitRunTicks(3);
        await pair.Client.WaitAssertion(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                close.Invoke(chat, null);
                Assert.That(window.Disposed, Is.False);
                open.Invoke(chat, null);
                Assert.That(retained.GetValue(chat), Is.SameAs(window));
            }
            var current = (int) session.GetValue(chat)!;
            var input = chat.ChatInput.Input;
            input.SetText("current draft");
            action.Invoke(chat, new object[] { "draft", new TguiData().Number("session", current - 1).String("text", "stale draft").ToString() });
            Assert.That(input.Text, Is.EqualTo("current draft"));
            action.Invoke(chat, new object[] { "draft", new TguiData().Number("session", current).String("text", "new draft").ToString() });
            Assert.That(input.Text, Is.EqualTo("new draft"));
            typeof(ChatBox).GetMethod("SetChatMode", Private)!.Invoke(chat, new object[] { true, true });
            Assert.That(retained.GetValue(chat), Is.Null);
            Assert.That(window.Disposed, Is.True);
            chat.Orphan();
            chat.Dispose();
        });
        await pair.CleanReturnAsync();
    }
}
