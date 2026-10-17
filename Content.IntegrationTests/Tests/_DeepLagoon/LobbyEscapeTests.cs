using System.Linq;
using Content.Client.Gameplay;
using Content.Client.Lobby;
using Content.Client.Options.UI;
using Content.Shared.Input;
using Robust.Client.Input;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LobbyEscapeTests
{
    [Test]
    public async Task EscapeOpensLobbyOptionsClosesRecentWindowsAndPreservesGameplayMenu()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var states = pair.Client.ResolveDependency<IStateManager>();
            var input = pair.Client.ResolveDependency<IInputManager>();
            var escape = input.GetInputCommand(ContentKeyFunctions.EscapeContext);
            Assert.That(escape, Is.Not.Null);
            void PressEscape()
            {
                escape!.Enabled(null);
                escape.Disabled(null);
            }

            void CloseStartupWindows()
            {
                // Isolate the bare screen from windows automatically opened on state entry.
                foreach (var window in ui.WindowRoot.Children.OfType<BaseWindow>().ToArray())
                    window.Close();
            }

            Assert.That(states.CurrentState, Is.TypeOf<LobbyState>());
            CloseStartupWindows();
            PressEscape();
            var options = ui.WindowRoot.Children.OfType<OptionsMenu>().Single();
            Assert.That(options.IsOpen, Is.True, "Esc must open settings directly from the lobby");

            using var otherWindow = ui.CreateWindow<DefaultWindow>();
            otherWindow.OpenCentered();
            PressEscape();
            Assert.That(otherWindow.IsOpen, Is.False, "Esc must first close the most recently opened window");
            Assert.That(options.IsOpen, Is.True);
            PressEscape();
            Assert.That(options.IsOpen, Is.False);
            PressEscape();
            Assert.That(ui.WindowRoot.Children.OfType<OptionsMenu>().Single(), Is.SameAs(options),
                "Reopening settings must reuse the existing window");
            PressEscape();

            states.RequestStateChange<GameplayState>();
            CloseStartupWindows();
            PressEscape();
            Assert.That(options.IsOpen, Is.False, "Gameplay must keep its game menu instead of opening settings");
            Assert.That(ui.WindowRoot.Children.OfType<EscapeMenu>().Single().IsOpen, Is.True);
            PressEscape();
            Assert.That(ui.WindowRoot.Children.OfType<EscapeMenu>().Any(), Is.False);

            states.RequestStateChange<LobbyState>();
            CloseStartupWindows();
            PressEscape();
            Assert.That(options.IsOpen, Is.True, "Esc must still open settings after returning to the lobby");
            PressEscape();
        });
        await pair.CleanReturnAsync();
    }
}
