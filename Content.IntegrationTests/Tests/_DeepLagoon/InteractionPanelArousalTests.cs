using System.Reflection;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class InteractionPanelArousalTests
{
    [Test]
    public async Task ExecutionAwardsBothOnceAndRespectsIndividualRecovery()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var user = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var target = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var panels = entities.System<InteractionPanelSystem>();
            var userArousal = entities.GetComponent<InteractionPanelArousalComponent>(user);
            var targetArousal = entities.GetComponent<InteractionPanelArousalComponent>(target);
            var action = new InteractionPanelActionDefinition("test", "all", InteractionPanelCategory.Neutral,
                Template: "{user} greets {target}.", ArousalGain: 8);
            void Execute(EntityUid other) =>
                typeof(InteractionPanelSystem).GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(panels, [user, other, action, null]);

            Execute(target);
            Assert.Multiple(() =>
            {
                Assert.That(userArousal.Current, Is.EqualTo(8), "The initiator receives the full amount");
                Assert.That(targetArousal.Current, Is.EqualTo(8), "The target receives the full amount");
            });

            Execute(user);
            Assert.That(userArousal.Current, Is.EqualTo(16), "Self interaction awards once");
            Assert.That(targetArousal.Current, Is.EqualTo(8));

            var recovery = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(30);
            targetArousal.RecoveryUntil = recovery;
            Execute(target);
            Assert.That(userArousal.Current, Is.EqualTo(24), "Target recovery does not block the initiator");
            Assert.That(targetArousal.Current, Is.EqualTo(8));

            targetArousal.RecoveryUntil = TimeSpan.Zero;
            userArousal.RecoveryUntil = recovery;
            Execute(target);
            Assert.That(userArousal.Current, Is.EqualTo(24));
            Assert.That(targetArousal.Current, Is.EqualTo(16), "Initiator recovery does not block the target");

            entities.DeleteEntity(user);
            entities.DeleteEntity(target);
        });
        await pair.CleanReturnAsync();
    }
}
