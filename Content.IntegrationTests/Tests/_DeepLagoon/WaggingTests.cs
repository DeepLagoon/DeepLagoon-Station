using Content.Server.Humanoid;
using Content.Server.Wagging;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Wagging;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using ActionsSystem = Content.Server.Actions.ActionsSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class WaggingTests
{
    [Test]
    public async Task OnlyAnimatedTailsGrantAndPerformWagging()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var resources = pair.Client.ResolveDependency<IResourceCache>();
            foreach (var prototype in prototypes.EnumeratePrototypes<MarkingPrototype>())
            {
                if (!WaggingMarkings.TryGetPair(prototypes, prototype.ID, WaggingMarkings.Suffix,
                        out _, out var animated) || prototype.ID != animated)
                    continue;

                var hasAnimation = false;
                foreach (var sprite in prototype.Sprites)
                {
                    Assert.That(sprite, Is.InstanceOf<SpriteSpecifier.Rsi>(), animated);
                    var rsi = (SpriteSpecifier.Rsi) sprite;
                    var resource = resources.GetResource<RSIResource>(new ResPath("/Textures") / rsi.RsiPath);
                    Assert.That(resource.RSI.TryGetState(rsi.RsiState, out var state), Is.True, animated);
                    hasAnimation |= state.IsAnimated;
                }
                Assert.That(hasAnimation, Is.True, $"{animated} must have real animation frames");
            }
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.ResolveDependency<IEntityManager>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var wagging = entities.System<WaggingSystem>();
            var appearance = entities.System<HumanoidAppearanceSystem>();
            var actions = entities.System<ActionsSystem>();
            var uid = entities.SpawnEntity("MobReptilian", MapCoordinates.Nullspace);
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
            var component = entities.GetComponent<WaggingComponent>(uid);
            bool HasAction() => actions.TryGetActionData(component.ActionEntity, out var action)
                && action.AttachedEntity == uid;
            void SetTail(string id)
            {
                humanoid.MarkingSet.Clear();
                appearance.AddMarking(uid, id, Color.Red, forced: true, humanoid: humanoid);
                wagging.Update(1);
            }

            SetTail("LizardTailLarge");
            Assert.That(HasAction(), Is.False, "Species-provided Wagging must not grant an unsupported action");
            Assert.That(wagging.TryToggleWagging(uid), Is.False);
            Assert.That(component.Wagging, Is.False);

            foreach (var id in new[] { "LizardTailSmooth", "VulpTail", "VoxTail", "SnakeLargeTail" })
            {
                SetTail(id);
                Assert.That(HasAction(), Is.True, id);
                var action = component.ActionEntity;
                wagging.Update(1);
                Assert.That(component.ActionEntity, Is.EqualTo(action), "Refresh must preserve the action");
                Assert.That(wagging.TryToggleWagging(uid), Is.True, id);
                var tail = humanoid.MarkingSet.Markings[MarkingCategories.Tail][0];
                Assert.That(tail.MarkingId, Is.EqualTo(id + "Animated"));
                Assert.That(tail.MarkingColors[0], Is.EqualTo(Color.Red));
                wagging.Update(1);
                Assert.That(HasAction(), Is.True, "Animated IDs must retain their action");
                Assert.That(wagging.TryToggleWagging(uid), Is.True);
                Assert.That(humanoid.MarkingSet.Markings[MarkingCategories.Tail][0].MarkingId, Is.EqualTo(id));
            }

            SetTail("LizardTailSmooth");
            appearance.AddMarking(uid, "LizardTailLarge", forced: true, humanoid: humanoid);
            Assert.That(wagging.TryToggleWagging(uid), Is.True);
            Assert.That(humanoid.MarkingSet.Markings[MarkingCategories.Tail][1].MarkingId,
                Is.EqualTo("LizardTailLarge"), "Unsupported additional markings must stay unchanged");
            SetTail("LizardTailLarge");
            Assert.That(HasAction(), Is.False);
            Assert.That(component.Wagging, Is.False, "Changing to an unsupported tail must stop wagging");
            humanoid.MarkingSet.Clear();
            wagging.Update(1);
            Assert.That(HasAction(), Is.False);
            Assert.That(wagging.TryToggleWagging(uid), Is.False);
            Assert.That(WaggingMarkings.TryGetPair(prototypes, "MissingTailAnimated", WaggingMarkings.Suffix,
                out _, out _), Is.False);
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }
}
