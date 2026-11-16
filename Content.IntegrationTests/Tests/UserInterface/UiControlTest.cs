using System.Linq;
using System.Numerics;
using Content.Client.Administration.UI.Tabs.AdminTab;
using Content.Client.Chat.UI;
using Content.Client.LateJoin;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.ContentPack;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Reflection;
using Content.Client._NF.CryoSleep; // Frontier

namespace Content.IntegrationTests.Tests.UserInterface;

[TestFixture]
public sealed class UiControlTest
{
    // You should not be adding to this.
    private Type[] _ignored = new Type[]
    {
        typeof(EmotesMenu),
        typeof(LateJoinGui),
        typeof(CryosleepWakeupWindow), // Frontier: FIXME - refactor this window into EUI(?) pattern, this thing subscribes to events
    };

    /// <summary>
    /// Tests that all windows can be instantiated successfully.
    /// </summary>
    [Test]
    public async Task TestWindows()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings()
        {
            Connected = true,
        });
        var activator = pair.Client.ResolveDependency<IDynamicTypeFactory>();
        var refManager = pair.Client.ResolveDependency<IReflectionManager>();
        var loader = pair.Client.ResolveDependency<IModLoader>();

        await pair.Client.WaitAssertion(() =>
        {
            foreach (var type in refManager.GetAllChildren(typeof(BaseWindow)))
            {
                if (type.IsAbstract || _ignored.Contains(type))
                    continue;

                if (!loader.IsContentType(type))
                    continue;

                // If it has no empty ctor then skip it instead of figuring out what args it needs.
                var ctor = type.GetConstructor(Type.EmptyTypes);

                if (ctor == null)
                    continue;

                // Don't inject because the control themselves have to do it.
                activator.CreateInstance(type, oneOff: true, inject: false);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TeleportWindowClosesFromTitleBar()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var client = pair.Client;
        TeleportWindow window = default!;
        await client.WaitPost(() => window = new TeleportWindow());

        for (var i = 0; i < 2; i++)
        {
            TextureButton closeButton = default!;
            ScreenCoordinates cursor = default;
            await client.WaitAssertion(() =>
            {
                window.Open(Vector2.Zero);
                window.Arrange(UIBox2.FromDimensions(Vector2.Zero, window.DesiredSize));
                closeButton = window.FindControl<TextureButton>("CloseButton");
                cursor = new ScreenCoordinates(closeButton.GlobalPixelPosition + closeButton.PixelSize / 2f,
                    closeButton.Window?.Id ?? default);
            });

            foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
            {
                await client.DoGuiEvent(closeButton, new GUIBoundKeyEventArgs(
                    EngineKeyFunctions.UIClick, state, cursor, default, Vector2.Zero, Vector2.Zero));
            }

            await client.WaitAssertion(() => Assert.That(window.IsOpen, Is.False));
        }

        await pair.CleanReturnAsync();
    }
}
