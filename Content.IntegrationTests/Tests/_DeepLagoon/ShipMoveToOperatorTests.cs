using Content.Server._Mono.NPC.HTN.Operators;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ShipMoveToOperatorTests
{
    [Test]
    public async Task MissingOwnerFailsPlanAndUpdateAndAllowsShutdown()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var move = new ShipMoveToOperator();
            move.Initialize(entities.EntitySysManager);
            var blackboard = new NPCBlackboard();
            blackboard.SetValue(move.TargetKey, EntityCoordinates.Invalid);

            Assert.That(move.Plan(blackboard, default).GetAwaiter().GetResult().Valid, Is.False);
            Assert.DoesNotThrow(() => move.Startup(blackboard));
            Assert.That(move.Update(blackboard, 0), Is.EqualTo(HTNOperatorStatus.Failed));
            Assert.DoesNotThrow(() => move.ConditionalShutdown(blackboard));
            var fire = new ShipFireGunsOperator();
            fire.Initialize(entities.EntitySysManager);
            Assert.DoesNotThrow(() => fire.PlanShutdown(blackboard));
            Assert.DoesNotThrow(() => move.PlanShutdown(blackboard));
            Assert.That(blackboard.ContainsKey(move.TargetKey), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}
