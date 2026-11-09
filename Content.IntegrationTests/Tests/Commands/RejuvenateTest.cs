using Content.Server.Administration.Commands;
using Content.Server.Administration.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.StatusEffect;
using Content.Shared.Stunnable;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Commands
{
    [TestFixture]
    [TestOf(typeof(RejuvenateSystem))]
    public sealed class RejuvenateTest
    {
        private static readonly ProtoId<DamageGroupPrototype> TestDamageGroup = "Toxin";

        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  name: DamageableDummy
  id: DamageableDummy
  components:
  - type: Damageable
    damageContainer: Biological
  - type: MobState
  - type: MobThresholds
    thresholds:
      0: Alive
      200: Dead
";

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task RejuvenateSleepingTest(bool forced, bool inBed)
        {
            await using var pair = await PoolManager.GetServerClient();
            var map = await pair.CreateTestMap();
            var server = pair.Server;
            var entities = server.EntMan;
            var sleeping = entities.System<SleepingSystem>();
            var buckle = entities.System<SharedBuckleSystem>();
            var blocker = entities.System<ActionBlockerSystem>();
            var human = EntityUid.Invalid;

            await server.WaitAssertion(() =>
            {
                human = entities.SpawnEntity("MobHuman", map.GridCoords);
                if (inBed)
                {
                    var bed = entities.SpawnEntity("Bed", map.GridCoords);
                    Assert.That(buckle.TryBuckle(human, human, bed), Is.True);
                }
            });

            if (inBed)
                await pair.RunTicksSync(30);

            await server.WaitAssertion(() =>
            {
                if (forced)
                {
                    var status = entities.System<StatusEffectsSystem>();
                    Assert.That(status.TryAddStatusEffect<ForcedSleepingComponent>(
                        human, "ForcedSleep", TimeSpan.FromMinutes(1), false), Is.True);
                }
                else
                {
                    Assert.That(sleeping.TrySleeping(human), Is.True);
                }

                Assert.That(entities.HasComponent<SleepingComponent>(human), Is.True);
                Assert.That(blocker.CanMove(human), Is.False);

                entities.System<RejuvenateSystem>().PerformRejuvenate(human);

                Assert.Multiple(() =>
                {
                    Assert.That(entities.HasComponent<SleepingComponent>(human), Is.False);
                    Assert.That(entities.HasComponent<ForcedSleepingComponent>(human), Is.False);
                    Assert.That(entities.HasComponent<KnockedDownComponent>(human), Is.False);
                });

                if (inBed)
                    Assert.That(buckle.TryUnbuckle(human, human), Is.True);

                Assert.That(blocker.CanMove(human), Is.True);
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task RejuvenateDeadTest()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var prototypeManager = server.ResolveDependency<IPrototypeManager>();
            var mobStateSystem = entManager.System<MobStateSystem>();
            var damSystem = entManager.System<DamageableSystem>();
            var rejuvenateSystem = entManager.System<RejuvenateSystem>();

            await server.WaitAssertion(() =>
            {
                var human = entManager.SpawnEntity("DamageableDummy", MapCoordinates.Nullspace);
                DamageableComponent damageable = null;
                MobStateComponent mobState = null;

                // Sanity check
                Assert.Multiple(() =>
                {
                    Assert.That(entManager.TryGetComponent(human, out damageable));
                    Assert.That(entManager.TryGetComponent(human, out mobState));
                });
                Assert.Multiple(() =>
                {
                    Assert.That(mobStateSystem.IsAlive(human, mobState), Is.True);
                    Assert.That(mobStateSystem.IsCritical(human, mobState), Is.False);
                    Assert.That(mobStateSystem.IsDead(human, mobState), Is.False);
                    Assert.That(mobStateSystem.IsIncapacitated(human, mobState), Is.False);
                });

                // Kill the entity
                DamageSpecifier damage = new(prototypeManager.Index(TestDamageGroup), FixedPoint2.New(10000000));

                damSystem.TryChangeDamage(human, damage, true);

                // Check that it is dead
                Assert.Multiple(() =>
                {
                    Assert.That(mobStateSystem.IsAlive(human, mobState), Is.False);
                    Assert.That(mobStateSystem.IsCritical(human, mobState), Is.False);
                    Assert.That(mobStateSystem.IsDead(human, mobState), Is.True);
                    Assert.That(mobStateSystem.IsIncapacitated(human, mobState), Is.True);
                });

                // Rejuvenate them
                rejuvenateSystem.PerformRejuvenate(human);

                // Check that it is alive and with no damage
                Assert.Multiple(() =>
                {
                    Assert.That(mobStateSystem.IsAlive(human, mobState), Is.True);
                    Assert.That(mobStateSystem.IsCritical(human, mobState), Is.False);
                    Assert.That(mobStateSystem.IsDead(human, mobState), Is.False);
                    Assert.That(mobStateSystem.IsIncapacitated(human, mobState), Is.False);

                    Assert.That(damageable.TotalDamage, Is.EqualTo(FixedPoint2.Zero));
                });
            });
            await pair.CleanReturnAsync();
        }
    }
}
