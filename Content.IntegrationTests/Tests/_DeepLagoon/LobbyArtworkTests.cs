using System.Linq;
using System.Numerics;
using Content.Client.Lobby.UI;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Server.AutoVote;
using Content.Server.Voting;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LobbyArtworkTests
{
    [Test]
    public async Task ServerInformationMovesAboveChatOnlyWhileItsFullTextFits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var lobby = new LobbyGui();
            const string blob = "Добро пожаловать на DeepLagoon Station!\nТекущий раунд: #169\nИгроков на сервере: 1 (готовы: 0)\nТекущий режим: Стандарт (ТСФ|ДФ)\n[color=yellow]Местные силы ДФ мешают колониальной экспансии ТСФ в сектор.[/color]";
            lobby.ServerName.Text = "Лобби: MyServer";
            lobby.StationTime.Text = "Раунд ещё не начался";
            lobby.RoundCountdown.Text = "01:13";
            lobby.SetServerInfo(blob);
            var info = lobby.ServerInfo;
            var text = info.Children.OfType<Robust.Client.UserInterface.Controls.RichTextLabel>().Single();
            void Arrange(Vector2 size)
            {
                lobby.MainContainer.SidePanelWidth = 600;
                lobby.MainContainer.InvalidateMeasure();
                lobby.RightSide.InvalidateMeasure();
                lobby.MainContainer.Measure(size);
                lobby.MainContainer.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
            }
            foreach (var (size, inline) in new[]
                     {
                         (new Vector2(2754, 1348), true),
                         (new Vector2(1024, 600), false),
                         (new Vector2(2560, 1440), true),
                         (new Vector2(1280, 720), false),
                         (new Vector2(3840, 2160), true),
                         (new Vector2(1920, 1080), false),
                     })
            {
                Arrange(size);
                Assert.That(info.Parent, Is.SameAs(inline ? lobby.InlineServerInfo : lobby.SeparateServerInfo), $"Viewport {size}");
                Assert.That(lobby.ServerConsole.Visible, Is.EqualTo(!inline));
                Assert.That(lobby.InlineServerInfo.Visible, Is.EqualTo(inline));
                Assert.That(lobby.ServerInfo, Is.SameAs(info), "Resize moves the live information control without duplicating it");
                Assert.That(text.GetMessage(), Does.Contain("#169").And.Contain("[color=yellow]"));
                Assert.That(lobby.ChatPanel.Height, Is.EqualTo(lobby.RightSide.Height * 0.85f).Within(1));
                if (inline)
                {
                    Assert.That(text.GlobalPosition.Y,
                        Is.GreaterThanOrEqualTo(lobby.RoundStatusHeading.GlobalPosition.Y + lobby.RoundStatusHeading.Height));
                    Assert.That(text.GlobalPosition.Y + text.Height,
                        Is.LessThanOrEqualTo(lobby.RoundStatusPanel.GlobalPosition.Y + lobby.RoundStatusPanel.Height - 9));
                }
            }
            lobby.SetServerInfo(string.Join('\n', Enumerable.Repeat("Длинное описание сервера без обрезания текста.", 30)));
            Arrange(new Vector2(3840, 2160));
            Assert.That(info.Parent, Is.SameAs(lobby.SeparateServerInfo), "Long information keeps the scrollable fallback even on a large monitor");
            lobby.UtilityDrawer.Collapsed = true;
            lobby.SetServerInfo(blob);
            // Use the same size and rely on the update's invalidation, rather
            // than the helper's forced resize, while the old host is hidden.
            var unchangedSize = new Vector2(3840, 2160);
            lobby.MainContainer.Measure(unchangedSize);
            lobby.MainContainer.Arrange(UIBox2.FromDimensions(Vector2.Zero, unchangedSize));
            Assert.That(info.Parent, Is.SameAs(lobby.InlineServerInfo), "Updated server information is remeasured at the same viewport size");
            Assert.That(lobby.UtilityDrawer.Collapsed, Is.True, "Moving server text preserves the drawer's chosen state");
            var header = lobby.Chat.FindControl<Robust.Client.UserInterface.Controls.PanelContainer>("LobbyChatHeading");
            Assert.That(header.PanelOverride, Is.TypeOf<Robust.Client.Graphics.StyleBoxFlat>());
            Assert.That(lobby.RoundStatusHeading.PanelOverride, Is.TypeOf<Robust.Client.Graphics.StyleBoxFlat>());
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LobbyRestartReplacesItsAutomaticVoteWithoutCancellingManualVotes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CCVars.AutoVoteEnabled, true);
            config.SetCVar(CCVars.MapAutoVoteEnabled, false);
            config.SetCVar(CCVars.PresetAutoVoteEnabled, true);
            var votes = pair.Server.ResolveDependency<Content.Server.Voting.Managers.IVoteManager>();
            foreach (var vote in votes.ActiveVotes.ToArray())
                vote.Cancel();
            votes.Update();
            var manual = votes.CreateVote(new VoteOptions
            {
                Title = "Manual vote survives lobby restart",
                Duration = TimeSpan.FromMinutes(3),
                Options = { ("Keep", "keep"), ("Skip", "skip") },
            });
            var automatic = pair.Server.ResolveDependency<IEntityManager>().System<AutoVoteSystem>();
            automatic.OnReturnedToLobby(new RoundRestartCleanupEvent());
            var first = votes.ActiveVotes.Single(vote => !vote.Finished && vote.Id != manual.Id);
            automatic.OnReturnedToLobby(new RoundRestartCleanupEvent());
            Assert.That(first.Finished, Is.True, "The previous lobby's automatic vote must be retired");
            Assert.That(votes.ActiveVotes.Count(vote => !vote.Finished && vote.Id != manual.Id), Is.EqualTo(1));
            Assert.That(manual.Finished, Is.False, "Unrelated manual votes must remain active");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LobbyUtilitiesFoldBesideChatWithCharacterActionsAndMusicOnTheLeft()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var lobby = new LobbyGui();
            var actions = lobby.FindControl<Control>("DefaultState");
            Assert.That(actions.Parent, Is.SameAs(lobby.LeftSide));
            Assert.That(lobby.UtilityScroll.Parent, Is.SameAs(lobby.UtilityDrawer));
            Assert.That(lobby.MusicPlayer.Parent, Is.SameAs(lobby.LeftSide));
            Assert.That(lobby.ChatPanel.Parent, Is.SameAs(lobby.RightSide));
            Assert.That(lobby.RoundStatusPanel.Parent, Is.SameAs(lobby.RightSide));
            Assert.That(lobby.ServerName.Parent, Is.SameAs(lobby.HeaderContainer));
            Assert.That(lobby.ChatPanel.UseConsoleStyle && lobby.RoundStatusPanel.UseConsoleStyle, Is.True);
            var setup = lobby.CharacterPreview.CharacterSetupButton;
            Assert.That(lobby.ButtonPanel.Children.Any(child => child == setup.Parent), Is.True);
            Assert.That(lobby.CharacterPreview.Parent, Is.SameAs(lobby.CenterContents));
            Assert.That(lobby.ReadyButton.Label.Visible || lobby.ObserveButton.Label.Visible || setup.Label.Visible, Is.False);
            lobby.CharacterPreview.SetLoaded(false);
            Assert.That(setup.Disabled, Is.True);
            lobby.CharacterPreview.SetLoaded(true);
            Assert.That(setup.Disabled, Is.False);
            lobby.CharacterPreview.SetSummaryText("Это Irma Grus. Ей 57 лет.");
            lobby.CharacterPreview.SetBankBalance(75000);
            lobby.CharacterPreview.SetCompanyText("Компания: DeepLagoon");
            lobby.CharacterPreview.SetLagoonCoinBalance(0);
            var bankCounter = lobby.CharacterPreview.FindControl<LobbySegmentCounter>("BankBalance");
            var coinCounter = lobby.CharacterPreview.FindControl<LobbySegmentCounter>("LagoonCoins");
            Assert.That(bankCounter.DisplayText, Is.EqualTo("75 000"));
            Assert.That(coinCounter.DisplayText, Is.EqualTo("0"));
            lobby.CharacterPreview.SetLagoonCoinBalance(long.MaxValue);
            Assert.That(coinCounter.Value, Is.EqualTo(long.MaxValue), "Account balances must never be truncated to an int");
            Assert.That(coinCounter.DisplayText, Is.EqualTo("9 223 372 036 854 775 807"));
            lobby.CharacterPreview.SetLagoonCoinBalance(-1);
            Assert.That(coinCounter.Value, Is.Null, "An unloaded balance must not look like zero");
            Assert.That(coinCounter.DisplayText, Is.EqualTo("-"));
            lobby.CharacterPreview.SetLagoonCoinBalance(0);
            Assert.That(lobby.CharacterPreview.FindControl<Control>("ViewBox").GetChild(0), Is.TypeOf<LobbyCharacterLight>());
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            lobby.CharacterPreview.SetSprite(entities.SpawnEntity("MobHumanDummy", Robust.Shared.Map.MapCoordinates.Nullspace));
            var characterFrame = lobby.CharacterPreview.FindControl<LobbyConsolePanel>("Frame");
            Assert.That(characterFrame.UseConsoleStyle, Is.True);
            Assert.That(characterFrame.ActualModulateSelf, Is.EqualTo(lobby.ServerConsole.ActualModulateSelf));
            var characterView = lobby.CharacterPreview.FindControl<Control>("ViewBox").Children
                .OfType<Robust.Client.UserInterface.Controls.SpriteView>().Single();
            lobby.ServerInfo.SetInfoBlob("Добро пожаловать на DeepLagoon Station!\nТекущий раунд: #19\nИгроков на сервере: 1\n[color=yellow]Местные силы ДФ мешают колониальной экспансии ТСФ в сектор.[/color]");
            Assert.That(lobby.ServerInfo.Parent, Is.SameAs(lobby.SeparateServerInfo));
            var consoleText = lobby.ServerInfo.Children.OfType<Robust.Client.UserInterface.Controls.RichTextLabel>().Single();
            Assert.That(consoleText.GetMessage(), Does.Contain("[color=yellow]"));
            var utilities = lobby.UtilityButtons.Children
                .OfType<Robust.Client.UserInterface.Controls.Button>().ToArray();
            Assert.That(utilities.Length, Is.GreaterThanOrEqualTo(8));
            Assert.That(utilities.All(button => button.MuteSounds && !button.Label.Visible), Is.True);
            Assert.That(lobby.ApartmentsButton.Parent, Is.SameAs(lobby.UtilityButtons));
            Assert.That(lobby.EconomyButton.Parent, Is.SameAs(lobby.UtilityButtons));
            Assert.That(Array.IndexOf(utilities, lobby.ApartmentsButton), Is.EqualTo(Array.IndexOf(utilities, lobby.EconomyButton) + 1));
            Assert.That(lobby.UtilityButtons.Parent?.Parent, Is.SameAs(lobby.UtilityTray));
            Assert.That(lobby.FindControl<Control>("LinkBanner").Parent?.Parent, Is.SameAs(lobby.UtilityTray));
            Assert.That(lobby.FindControl<Control>("DevInfoBanner").Parent?.Parent, Is.SameAs(lobby.UtilityTray));
            Assert.That(lobby.UtilityTray.GetChild(lobby.UtilityTray.ChildCount - 1), Is.TypeOf<LobbyConsoleScanlines>(),
                "CRT lines must draw above content and remain transparent to mouse input");
            Assert.That(lobby.UtilityTray.GetChild(lobby.UtilityTray.ChildCount - 1).MouseFilter,
                Is.EqualTo(Control.MouseFilterMode.Ignore));
            var popup = new Content.Client.Voting.UI.VotePopup(new Content.Client.Voting.VoteManager.ActiveVote(123)
            {
                Title = "Следующий режим игры",
                Initiator = "Сервер",
                Entries = [new Content.Client.Voting.VoteManager.VoteEntry("Стандарт"), new Content.Client.Voting.VoteManager.VoteEntry("Динамический")],
            });
            var bar = popup.FindControl<LobbyVoteProgressBar>("TimeLeftBar");
            Assert.That(bar.UseLobbyStyle, Is.False, "Votes outside the lobby retain the standard style");
            var voteFrame = popup.FindControl<LobbyConsolePanel>("Frame");
            // A detached test control has not reached the UI's queued style pass.
            // Apply it explicitly so the test observes the same tint as rendering.
            voteFrame.DoStyleUpdate();
            Assert.That(voteFrame.ActualModulateSelf, Is.EqualTo(Color.FromHex("#292929")),
                "The standard vote frame retains its AngleRect tint outside the lobby");
            lobby.VoteContainer.AddChild(popup);
            popup.UpdateData();
            voteFrame.DoStyleUpdate();
            Assert.That(bar.UseLobbyStyle, Is.True);
            Assert.That(voteFrame.ActualModulateSelf, Is.EqualTo(lobby.ServerConsole.ActualModulateSelf),
                "The console housing must not inherit the standard vote frame's dark tint");
            Assert.That(voteFrame.ActualModulateSelf, Is.EqualTo(lobby.UtilityTray.ActualModulateSelf));
            Assert.That(voteFrame.ActualModulateSelf, Is.EqualTo(Color.White));
            Assert.That(popup.FindControl<Control>("ScreenScanlines").Visible, Is.True);
            var options = popup.FindControl<Robust.Client.UserInterface.Controls.GridContainer>("VoteOptionsContainer");
            Assert.That(options.Columns, Is.EqualTo(1));
            Assert.That(options.ChildCount, Is.EqualTo(2));
            var choice = options.Children.OfType<Robust.Client.UserInterface.Controls.Button>().First();
            Assert.That(choice.Label.Visible && choice.Label.ClipText && choice.MuteSounds, Is.True);
            var childCount = choice.ChildCount;
            popup.ApplyLobbyStyle();
            Assert.That(choice.ChildCount, Is.EqualTo(childCount), "Restyling must not duplicate artwork or callbacks");

            foreach (var size in new[] { new Vector2(1920, 1080), new Vector2(1280, 720), new Vector2(1024, 600), new Vector2(800, 600) })
            foreach (var chatWidth in new[] { 400, 600 })
            {
                lobby.MainContainer.SidePanelWidth = chatWidth;
                popup.FindControl<Robust.Client.UserInterface.Controls.Label>("TimeLeftText").Text = size.X >= 1280 ? "99:59" : "0:00";
                lobby.MainContainer.Measure(size);
                lobby.MainContainer.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                Assert.That(lobby.RightSide.Position.X + lobby.RightSide.Width,
                    Is.EqualTo(lobby.MainContainer.Width).Within(0.001f));
                Assert.That(lobby.ButtonPanel.GlobalPosition.X + lobby.ButtonPanel.Width / 2,
                    Is.EqualTo(lobby.LeftSide.GlobalPosition.X + lobby.LeftSide.Width / 2).Within(8));
                Assert.That(lobby.CharacterPreview.Position.Y + lobby.CharacterPreview.Height,
                    Is.LessThanOrEqualTo(lobby.ButtonPanel.Position.Y));
                Assert.That(lobby.UtilityDrawer.GlobalPosition.X + lobby.UtilityDrawer.Width,
                    Is.LessThan(lobby.ChatPanel.GlobalPosition.X));
                Assert.That(lobby.ChatPanel.Height, Is.EqualTo(lobby.MainContainer.Height * 0.85f).Within(1),
                    "Chat is 15 percent shorter, with round status above it");
                Assert.That(lobby.ChatPanel.Position.Y + lobby.ChatPanel.Height,
                    Is.EqualTo(lobby.RightSide.Height).Within(1));
                Assert.That(lobby.ChatPanel.Position.Y - lobby.RoundStatusPanel.Height,
                    Is.EqualTo(LobbyConsoleTheme.BlockGap).Within(1));
                Assert.That(characterView.Size, Is.EqualTo(new Vector2(LobbyCharacterPreviewPanel.PreviewSize)),
                    $"The character must remain large at viewport {size} and chat width {chatWidth}");
                Assert.That(characterFrame.Width, Is.EqualTo(LobbyButtonPanel.BackplateWidth).Within(0.001f),
                    "Character and main-button housings must have the same visible width");
                Assert.That(characterFrame.GlobalPosition.X,
                    Is.EqualTo(lobby.ButtonPanel.GlobalPosition.X + (lobby.ButtonPanel.Width - characterFrame.Width) / 2).Within(0.001f));
                Assert.That(characterView.GlobalPosition.X, Is.GreaterThanOrEqualTo(characterFrame.GlobalPosition.X));
                Assert.That(characterView.GlobalPosition.X + characterView.Width,
                    Is.LessThanOrEqualTo(characterFrame.GlobalPosition.X + characterFrame.Width));
                var heading = lobby.CharacterPreview.FindControl<Control>("Header");
                Assert.That(heading.HorizontalAlignment, Is.EqualTo(Control.HAlignment.Center));
                Assert.That(popup.GlobalPosition.X,
                    Is.EqualTo(lobby.UtilityDrawer.GlobalPosition.X).Within(1), "Votes must stay beside chat");
                Assert.That(popup.Width, Is.EqualTo(lobby.UtilityTray.Width).Within(1), "Vote cards must fill the same sidebar width as the utility controls");
                var timer = bar.Parent!.Children.OfType<LobbyVoteTimer>().Single();
                Assert.That(timer.GlobalPosition.Y + timer.Height, Is.LessThanOrEqualTo(bar.GlobalPosition.Y),
                    "The timer must be above the bar, never in the same horizontal row");
                Assert.That(timer.GlobalPosition.X + timer.Width / 2,
                    Is.EqualTo(bar.GlobalPosition.X + bar.Width / 2).Within(1), "Timer and bar share the same center");
                Assert.That(bar.GlobalPosition.X + bar.Width / 2,
                    Is.EqualTo(popup.GlobalPosition.X + popup.Width / 2).Within(1), "Timer width cannot shift the progress bar");
                if (lobby.ServerConsole.Visible)
                    Assert.That(lobby.UtilityTray.Width, Is.EqualTo(lobby.ServerConsole.Width).Within(1));
                Assert.That(lobby.ButtonPanel.GlobalPosition.X + lobby.ButtonPanel.Width / 2,
                    Is.EqualTo(lobby.CharacterPreview.GlobalPosition.X + lobby.CharacterPreview.Width / 2).Within(1));
                var chatSize = lobby.ChatPanel.Size;
                var chatPosition = lobby.ChatPanel.GlobalPosition;
                var tabPosition = lobby.UtilityDrawer.ToggleButton.GlobalPosition;
                var characterPosition = lobby.CharacterPreview.GlobalPosition;
                var musicPosition = lobby.MusicPlayer.GlobalPosition;
                Assert.That(lobby.MusicPlayer.Position.Y + lobby.MusicPlayer.Height, Is.EqualTo(lobby.LeftSide.Height).Within(1));
                Assert.That(lobby.LeftSide.Position.X + lobby.LeftSide.Width, Is.LessThan(lobby.UtilityDrawer.Position.X));
                lobby.UtilityDrawer.Collapsed = true;
                lobby.MainContainer.Measure(size);
                lobby.MainContainer.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                Assert.That(lobby.UtilityScroll.Visible, Is.False);
                Assert.That(actions.Visible, Is.True);
                Assert.That(lobby.CharacterPreview.GlobalPosition, Is.EqualTo(characterPosition));
                Assert.That(lobby.MusicPlayer.GlobalPosition, Is.EqualTo(musicPosition));
                Assert.That(lobby.UtilityDrawer.Width, Is.EqualTo(LobbyUtilityDrawer.TabWidth));
                Assert.That(lobby.ChatPanel.Size, Is.EqualTo(chatSize));
                Assert.That(lobby.ChatPanel.GlobalPosition, Is.EqualTo(chatPosition));
                Assert.That(Vector2.Distance(lobby.UtilityDrawer.ToggleButton.GlobalPosition, tabPosition), Is.LessThan(0.001f));
                lobby.SwitchState(LobbyGui.LobbyGuiState.Default);
                Assert.That(lobby.UtilityScroll.Visible, Is.False, "Lobby refresh cannot reopen folded utilities");
                Assert.That(actions.Visible, Is.True);
                lobby.UtilityDrawer.Collapsed = false;
                lobby.MainContainer.Measure(size);
                lobby.MainContainer.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                Assert.That(actions.Visible, Is.True);
                Assert.That(characterView.Size, Is.EqualTo(new Vector2(LobbyCharacterPreviewPanel.PreviewSize)));
            }

            var cache = pair.Client.ResolveDependency<IResourceCache>();
            var background = cache.GetResource<RSIResource>("/Textures/_DeepLagoon/Interface/Lobby/background.rsi").RSI;
            Assert.That(background.Size, Is.EqualTo(new Vector2i(151, 123)));
            var ready = cache.GetResource<RSIResource>("/Textures/_DeepLagoon/Interface/Lobby/ready.rsi").RSI;
            Assert.That(ready["not_ready_pressed"].DelayCount, Is.EqualTo(7));
            Assert.That(ready["ready_pressed"].AnimationLength, Is.EqualTo(0.6f).Within(0.001f));
            var setupArt = cache.GetResource<RSIResource>("/Textures/_DeepLagoon/Interface/Lobby/character_setup.rsi").RSI;
            Assert.That(setupArt["character_setup_enabled"].DelayCount, Is.EqualTo(12));
            var observe = cache.GetResource<RSIResource>("/Textures/_DeepLagoon/Interface/Lobby/observe.rsi").RSI;
            Assert.That(observe.TryGetState("observe_disabled", out _), Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
