using Content.Server._Mono.NPC.HTN.Operators;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server._Mono.NPC.HTN;
using Content.Server.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ShipMoveToOperatorTests
{
    [Test]
    public async Task RuntimeOperatorInitializesDependenciesAndControlsTheShuttle()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            var owner = entities.SpawnEntity(null, map.GridCoords);
            var target = new EntityCoordinates(map.MapUid, 100, 0);
            var blackboard = new NPCBlackboard();
            blackboard.SetValue(NPCBlackboard.Owner, owner);
            var resumed = new ShipMoveToOperator();
            Assert.That(resumed.Update(blackboard, 0), Is.EqualTo(HTNOperatorStatus.Failed));
            var move = new ShipMoveToOperator { RequireAnchored = false, RequirePowered = false };
            blackboard.SetValue(move.TargetKey, target);
            Assert.That(move.Plan(blackboard, default).GetAwaiter().GetResult().Valid, Is.True);
            move.Startup(blackboard);
            Assert.That(entities.HasComponent<ShipSteererComponent>(owner), Is.True);
            Assert.That(move.Update(blackboard, 0), Is.EqualTo(HTNOperatorStatus.Continuing));
            move.ConditionalShutdown(blackboard);
            Assert.That(entities.HasComponent<ShipSteererComponent>(owner), Is.False);
        });
        await pair.CleanReturnAsync();
    }

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
