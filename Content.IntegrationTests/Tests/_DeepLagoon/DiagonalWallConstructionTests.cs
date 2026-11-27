using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Construction.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Shapes;
using Robust.Shared.Physics.Collision.Shapes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DiagonalWallConstructionTests : InteractionTest
{
    [TestCase("wallSolidDiagonal", "WallSolidDiagonal", 0)]
    [TestCase("wallSolidDiagonal", "WallSolidDiagonal", 90)]
    [TestCase("wallReinforcedDiagonal", "WallReinforcedDiagonal", 180)]
    [TestCase("wallPlastitaniumDiagonal", "WallPlastitaniumDiagonal", 270)]
    public async Task SelectedRecipeBuildsTheDiagonalEntity(string recipe, string result, int rotation)
    {
        await StartConstruction(recipe);
        await InteractUsing(Steel, 2);
        AssertPrototype("Girder");
        await Server.WaitPost(() => Transform.SetLocalRotation(STarget!.Value, Angle.FromDegrees(rotation)));

        // Steel would normally take the first square-wall edge. The selected recipe must stay selected.
        await InteractUsing(Steel, 2);
        AssertPrototype("Girder");
        await Server.WaitAssertion(() => Assert.That(SConstruction.GetTargetNode(STarget!.Value,
            SEntMan.GetComponent<ConstructionComponent>(STarget.Value))!.Name, Is.EqualTo(recipe)));

        if (recipe == "wallSolidDiagonal")
        {
            await InteractUsing(Cut);
            await InteractUsing(Steel, 2);
        }
        else
        {
            await InteractUsing("Plasteel", 2);
            AssertPrototype("ReinforcedGirder");
            if (recipe == "wallReinforcedDiagonal")
            {
                await InteractUsing(Screw);
                await InteractUsing("Plasteel", 2);
            }
            else
            {
                await Interact(Pry, Weld);
                await InteractUsing("Plasteel", 2);
                await InteractUsing("Plastitanium", 2);
            }
        }
        AssertPrototype(result);
        await Server.WaitAssertion(() =>
        {
            var target = STarget!.Value;
            Assert.That(SEntMan.GetComponent<TransformComponent>(target).LocalRotation.Degrees,
                Is.EqualTo(Angle.FromDegrees(rotation).Degrees).Within(0.01));
            Assert.That(SEntMan.GetComponent<FixturesComponent>(target).Fixtures["fix1"].Shape,
                Is.TypeOf<PolygonShape>());
        });
    }
}
