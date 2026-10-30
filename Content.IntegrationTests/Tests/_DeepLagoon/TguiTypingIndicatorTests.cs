using System.Linq;
using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Gameplay;
using Content.Client.UserInterface.Systems.Chat.Widgets;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared.Chat;
using Content.Shared.Chat.TypingIndicator;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.State;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TguiTypingIndicatorTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task ComposerDraftsReachServerAndAnimateThePlayer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid body = default;
        ChatBox chat = null;
        var action = typeof(ChatBox).GetMethod("HandleComposerAction", Private)!;
        var close = typeof(ChatBox).GetMethod("CloseComposer", Private)!;
        var sessionField = typeof(ChatBox).GetField("_composerSession", Private)!;
        var session = 0;
        await pair.Server.WaitAssertion(() =>
        {
            body = pair.Server.EntMan.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), body);
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitPost(() => pair.Client.ResolveDependency<IStateManager>().RequestStateChange<GameplayState>());
        await pair.RunTicksSync(3);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(pair.Client.PlayerMan.LocalEntity, Is.Not.Null);
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "Chat=1");
            chat = new ChatBox();
            pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot.AddChild(chat);
            chat.SafelySelectChannel(ChatSelectChannel.Local);
            typeof(ChatBox).GetMethod("OpenComposer", Private)!.Invoke(chat, null);
            session = (int) sessionField.GetValue(chat)!;
        });

        void Act(string name, string text, ChatSelectChannel channel)
        {
            action.Invoke(chat, new object[] { name, new TguiData()
                .Number("session", session).String("text", text)
                .String("channel", ((uint) channel).ToString()).ToString() });
        }

        async Task Check(TypingIndicatorState expected)
        {
            await pair.RunTicksSync(10);
            await pair.Server.WaitAssertion(() =>
            {
                var appearance = pair.Server.EntMan.System<SharedAppearanceSystem>();
                Assert.That(appearance.TryGetData<TypingIndicatorState>(body, TypingIndicatorVisuals.State, out var state), Is.True);
                Assert.That(state, Is.EqualTo(expected), "The TGUI action must reach the server, not just save a draft.");
            });
            await pair.Client.WaitAssertion(() =>
            {
                var uid = pair.Client.PlayerMan.LocalEntity!.Value;
                var sprite = pair.Client.EntMan.GetComponent<SpriteComponent>(uid);
                var layer = sprite[sprite.LayerMapGet(TypingIndicatorLayers.Base)];
                Assert.That(layer.Visible, Is.EqualTo(expected != TypingIndicatorState.None));
                Assert.That(layer.AutoAnimated, Is.EqualTo(expected.IsTyping()));
            });
        }

        foreach (var (channel, typing, idle) in new[] {
                     (ChatSelectChannel.Local, TypingIndicatorState.Typing, TypingIndicatorState.Idle),
                     (ChatSelectChannel.Emotes, TypingIndicatorState.EmoteTyping, TypingIndicatorState.EmoteIdle),
                     (ChatSelectChannel.LOOC, TypingIndicatorState.LoocTyping, TypingIndicatorState.LoocIdle),
                 })
        {
            await pair.Client.WaitAssertion(() =>
            {
                Act("channel", "", channel);
                Assert.That(chat.SelectedChannel, Is.EqualTo(channel));
                Act("focus", "", channel);
                Act("draft", "Новый текст из TGUI", channel);
                Assert.That(chat.ChatInput.Input.Text, Is.EqualTo("Новый текст из TGUI"));
            });
            await Check(typing);
            await pair.Client.WaitAssertion(() => Act("draft", "", channel));
            await Check(idle);
            await pair.Client.WaitAssertion(() => Act("draft", "Продолжение ввода", channel));
            await Check(typing);
            await pair.Client.WaitAssertion(() => Act("blur", "", channel));
            await Check(TypingIndicatorState.None);
        }
        await pair.Client.WaitAssertion(() =>
        {
            Act("focus", "", ChatSelectChannel.LOOC);
            Act("draft", "Последний черновик", ChatSelectChannel.LOOC);
        });
        await Check(TypingIndicatorState.LoocTyping);
        await pair.Client.WaitAssertion(() => close.Invoke(chat, null));
        await Check(TypingIndicatorState.None);
        await pair.Client.WaitAssertion(() =>
        {
            chat.Orphan();
            chat.Dispose();
        });
        await pair.CleanReturnAsync();
    }
}
