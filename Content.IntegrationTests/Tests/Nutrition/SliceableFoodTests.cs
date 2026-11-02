using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests.Nutrition;

public sealed class SliceableFoodTests : InteractionTest
{
    [TestCase("FoodBreadPlain", "FoodBreadPlainSlice", 5)]
    [TestCase("FoodBreadCotton", "FoodBreadCottonSlice", 5)]
    [TestCase("FoodBreadBaguette", "FoodBreadBaguetteSlice", 12)]
    [TestCase("FoodCheese", "FoodCheeseSlice", 4)]
    public async Task KnifeSlicingLeavesPiecesAtRest(string food, string slicePrototype, int count)
    {
        await SpawnTarget(food);
        var original = STarget!.Value;
        var origin = MapCoordinates.Nullspace;
        await Server.WaitPost(() => origin = Transform.GetMapCoordinates(original));

        await InteractUsing("KitchenKnife");

        EntityUid[] slices = [];
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.Deleted(original), Is.True, "The knife must finish slicing the food.");
            slices = SEntMan.EntityQuery<MetaDataComponent>()
                .Where(meta => meta.EntityPrototype?.ID == slicePrototype)
                .Select(meta => meta.Owner)
                .ToArray();
            Assert.That(slices, Has.Length.EqualTo(count));
            AssertSlicesAtRest();
        });

        // Check actual movement as well as initial velocity, including bread's projectile physics.
        await RunTicks(30);
        await Server.WaitAssertion(AssertSlicesAtRest);
        return;

        void AssertSlicesAtRest()
        {
            foreach (var slice in slices)
            {
                var position = Transform.GetMapCoordinates(slice);
                Assert.That(position.MapId, Is.EqualTo(origin.MapId));
                Assert.That(Vector2.Distance(position.Position, origin.Position), Is.LessThan(0.01f),
                    "Slicing must leave each piece where the food was cut.");
                Assert.That(SEntMan.GetComponent<PhysicsComponent>(slice).LinearVelocity,
                    Is.EqualTo(Vector2.Zero), "Slicing must not launch the pieces.");
            }
        }
    }
}
