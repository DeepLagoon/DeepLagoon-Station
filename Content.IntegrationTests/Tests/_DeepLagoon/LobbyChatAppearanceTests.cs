using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client.Lobby.UI;
using Content.Shared.Chat;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LobbyChatAppearanceTests
{
    [Test]
    public async Task NativeInputChannelsAndModeSwitchKeepTheirFontsAndDraft()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        LobbyGui lobby = null;
        Button modeSwitch = null;
        const string draft = "Проверка видимого текста 123";

        void CheckNative()
        {
            var chat = lobby.Chat;
            chat.ForceRunStyleUpdate();
            chat.Measure(new Vector2(480, 700));
            chat.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(480, 700)));
            Assert.That(chat.ChatInput.Visible, Is.True);
            Assert.That(chat.ChatInput.Input.Text, Is.EqualTo(draft));
            Assert.That(chat.ChatInput.ChannelSelector.SelectedChannel, Is.EqualTo(ChatSelectChannel.OOC));
            Assert.That(chat.ChatInput.ChannelSelector.Text, Is.Not.Empty);
            Assert.That(modeSwitch.Text, Is.EqualTo("TGUI"));
            Assert.That(modeSwitch.Label.Visible, Is.True);
            foreach (var control in new Robust.Client.UserInterface.Control[]
                     { chat.ChatInput.Input, chat.ChatInput.ChannelSelector.Label, modeSwitch.Label })
            {
                Assert.That(control.TryGetStyleProperty<Font>("font", out var font), Is.True,
                    $"{control.GetType().Name} must retain its native font after lobby styling");
                Assert.That(font.GetHeight(control.UIScale), Is.GreaterThan(0));
                Assert.That(control.Width, Is.GreaterThan(0));
                Assert.That(control.Height, Is.GreaterThan(0));
            }
        }

        static void Press(BaseButton button)
        {
            var callback = (Action<BaseButton.ButtonEventArgs>) typeof(BaseButton)
                .GetField("OnPressed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button)!;
            callback(new BaseButton.ButtonEventArgs(button, null!));
        }

        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "Chat=2");
            cfg.SetCVar(WebUiCVars.VanillaChat, true);
            lobby = new LobbyGui();
            var heading = lobby.Chat.ChatWindowPanel.GetChild(0).GetChild(0);
            modeSwitch = heading.GetChild(0).Children.OfType<Button>().Single();
            lobby.Chat.ChatInput.ChannelSelector.Select(ChatSelectChannel.OOC);
            lobby.Chat.ChatInput.Input.SetText(draft);
            CheckNative();
        });

        for (var i = 0; i < 3; i++)
        {
            await pair.Client.WaitAssertion(() => Press(modeSwitch));
            await pair.Client.WaitRunTicks(2);
            await pair.Client.WaitAssertion(() =>
            {
                Assert.That(modeSwitch.Text, Is.EqualTo("SS14"));
                Assert.That(lobby.Chat.ChatInput.Visible, Is.False);
                Assert.That(lobby.Chat.WebChat, Is.Not.Null);
                Assert.That(lobby.Chat.ChatInput.Input.Text, Is.EqualTo(draft));
                lobby.Chat.ForceRunStyleUpdate();
                Assert.That(modeSwitch.Label.TryGetStyleProperty<Font>("font", out _), Is.True);
                Press(modeSwitch);
            });
            await pair.Client.WaitRunTicks(2);
            await pair.Client.WaitAssertion(CheckNative);
        }

        await pair.Client.WaitAssertion(() =>
        {
#pragma warning disable CS0618
            lobby.Dispose();
#pragma warning restore CS0618
        });
        await pair.CleanReturnAsync();
    }
}
