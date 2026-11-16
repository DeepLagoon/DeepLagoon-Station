#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client.Humanoid;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Moq;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterPreviewWingLayerTests
{
    private static readonly ProtoId<MarkingPrototype> StrawberryWings = "BlueMoonDeco_wingsStrawberryChest";

    [Test]
    public async Task WingMarkingsRenderAboveAllLayersOnlyWhenFacingBack()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController)
                .GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            var mark = pair.Client.ResolveDependency<IPrototypeManager>()
                .Index(StrawberryWings).AsMarking();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Moth");
            editor.SetProfile(profile.WithCharacterAppearance(profile.Appearance.WithMarkings([mark])), editor.CharacterSlot);

            var view = (CharacterPreviewSpriteView) typeof(HumanoidProfileEditor)
                .GetProperty("SpriteView", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(editor)!;
            view.SetEntity(editor.PreviewDummy);
            var sprite = view.Sprite!;
            var original = sprite.LayerData.ToArray();
            var wings = original.Where(layer => layer.Visible && layer.ActualState?.StateId.Name?.Contains("wings_strawberry") == true).ToArray();
            Assert.That(wings, Has.Length.EqualTo(2));

            var texture = new Mock<Texture>(new Vector2i(32, 32)).Object;
            var world = new Mock<DrawingHandleWorld>(texture).Object;
            var render = new Mock<IRenderHandle>();
            var draws = 0;
            render.SetupGet(handle => handle.DrawingHandleWorld).Returns(world);
            render.Setup(handle => handle.DrawEntity(editor.PreviewDummy, It.IsAny<Vector2>(), It.IsAny<Vector2>(),
                    It.IsAny<Angle?>(), It.IsAny<Angle>(), It.IsAny<Direction?>(), sprite,
                    It.IsAny<TransformComponent?>(), It.IsAny<SharedTransformSystem?>(),
                    It.IsAny<IReadOnlyList<SpriteComponent.PostShaderEntry>?>()))
                .Callback(() =>
                {
                    draws++;
                    if (view.OverrideDirection == Direction.North)
                    {
                        Assert.That(wings.All(layer => !layer.Visible), Is.True);
                        Assert.That(sprite.LayerData.Skip(original.Length).Select(layer => layer.ActualState),
                            Is.EqualTo(wings.Select(layer => layer.ActualState)));
                    }
                    else
                    {
                        Assert.That(sprite.LayerData, Is.EqualTo(original));
                        Assert.That(wings.All(layer => layer.Visible), Is.True);
                    }
                });

            var draw = typeof(CharacterPreviewSpriteView)
                .GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
            var directions = new[] { Direction.South, Direction.East, Direction.North, Direction.West, Direction.North };
            foreach (var direction in directions)
            {
                view.OverrideDirection = direction;
                draw.Invoke(view, [render.Object]);
                Assert.That(sprite.LayerData, Is.EqualTo(original));
                Assert.That(wings.All(layer => layer.Visible), Is.True);
            }
            Assert.That(draws, Is.EqualTo(directions.Length));
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }
}
