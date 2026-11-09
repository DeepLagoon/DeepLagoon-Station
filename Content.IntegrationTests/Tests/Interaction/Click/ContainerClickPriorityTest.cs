using System.Numerics;
using Content.Client.Gameplay;
using Robust.Client.State;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.Interaction.Click;

public sealed class ContainerClickPriorityTest : InteractionTest
{
    protected override string PlayerPrototype => "ContainerClickPriorityTestMob";

    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          parent: InteractionTestMob
          id: ContainerClickPriorityTestMob
          components:
          - type: Sprite
            sprite: Structures/Storage/Crates/generic.rsi
            state: closed
            drawdepth: Mobs
          - type: Clickable
            bounds:
              all: "-0.4,-0.4,0.4,0.4"
        """;

    [Test]
    public async Task CrateNorthOfPlayerWinsWhenBothCaptureCursor()
    {
        var states = Client.ResolveDependency<IStateManager>();
        var eye = new Eye();
        var crateOffset = new Vector2(0, 0.25f);

        await Client.WaitAssertion(() =>
        {
            var state = (GameplayState) states.CurrentState;
            Assert.That(state.GetClickedEntity(Cursor(), eye), Is.EqualTo(CPlayer));
        });

        NetEntity crate = default;
        await Server.WaitPost(() =>
        {
            var uid = SEntMan.SpawnEntity("CrateGenericSteel", SEntMan.GetCoordinates(PlayerCoords).Offset(crateOffset));
            crate = SEntMan.GetNetEntity(uid);
        });
        await Pair.ReallyBeIdle(5);

        await Client.WaitAssertion(() =>
        {
            var state = (GameplayState) states.CurrentState;
            var cursor = Cursor();
            Assert.That(state.GetClickableEntities(cursor, eye), Does.Contain(CPlayer));
            Assert.That(state.GetClickedEntity(cursor, eye), Is.EqualTo(CEntMan.GetEntity(crate)));
        });

        MapCoordinates Cursor()
        {
            var player = CEntMan.System<SharedTransformSystem>().GetMapCoordinates(CPlayer);
            return new MapCoordinates(player.Position + crateOffset, player.MapId);
        }
    }
}
