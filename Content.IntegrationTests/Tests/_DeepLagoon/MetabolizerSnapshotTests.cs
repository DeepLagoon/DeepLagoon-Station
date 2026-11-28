#nullable enable annotations
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared.Body.Prototypes;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Robust.Shared.Collections;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed partial class MetabolizerSnapshotTests
{
    private const string Reagent = "DLTestMetabolizerSnapshot";
    private const string Metabolizer = "DLTestMetabolizerSnapshotEntity";

    [TestPrototypes]
    private const string Prototypes = """
        - type: reagent
          id: DLTestMetabolizerSnapshot
          parent: Water

        - type: entity
          id: DLTestMetabolizerSnapshotEntity
          components:
          - type: Metabolizer
            solutionOnBody: false
            solution: snapshot
            groups:
            - id: Medicine
          - type: SolutionContainerManager
            solutions:
              snapshot:
                maxVol: 10
                reagents:
                - ReagentId: DLTestMetabolizerSnapshot
                  Quantity: 10
        """;

    private sealed partial class SnapshotEffect : EntityEffect
    {
        public Action<EntityEffectBaseArgs> Callback = default!;
        public override void Effect(EntityEffectBaseArgs args) => Callback(args);
        protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
    }

    [Test]
    public async Task RegistryMutationAndNestedMetabolismPreserveTheSnapshotAndReleaseReferences()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        await pair.Server.WaitAssertion(() =>
        {
            var maps = em.System<SharedMapSystem>();
            var map = maps.CreateMap(out _, runMapInit: true);
            var system = em.System<MetabolizerSystem>();
            var solutions = em.System<SharedSolutionContainerSystem>();
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            var reagent = pair.Server.ProtoMan.Index<ReagentPrototype>(Reagent);
            var previous = reagent.Metabolisms;
            var effect = new SnapshotEffect();
            reagent.Metabolisms = new Dictionary<ProtoId<MetabolismGroupPrototype>, ReagentEffectsEntry>
            {
                ["Medicine"] = new() { MetabolismRate = FixedPoint2.New(1), Effects = [effect] },
            }.ToFrozenDictionary();
            var fixtures = new List<(EntityUid Uid, MetabolizerComponent Metabolizer, Solution Solution)>();

            (EntityUid Uid, MetabolizerComponent Metabolizer, Solution Solution) CreateMetabolizer()
            {
                var uid = em.SpawnEntity(Metabolizer, new EntityCoordinates(map, System.Numerics.Vector2.Zero));
                var component = em.GetComponent<MetabolizerComponent>(uid);
                SetDue(component);
                Assert.That(solutions.TryGetSolution(uid, "snapshot", out _, out var solution), Is.True);
                var fixture = (uid, component, solution!);
                fixtures.Add(fixture);
                return fixture;
            }

            void SetDue(MetabolizerComponent component) => typeof(MetabolizerComponent)
                .GetField(nameof(MetabolizerComponent.NextUpdate))!.SetValue(component, now);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var snapshotField = typeof(MetabolizerSystem).GetField("_metabolizerSnapshot", flags)!;
            void AssertReleased()
            {
                var snapshot = (ValueList<(EntityUid Uid, MetabolizerComponent Component)>) snapshotField.GetValue(system)!;
                Assert.That(snapshot.Count, Is.Zero);
                Assert.That(snapshot.Capacity, Is.GreaterThanOrEqualTo(2));
                var array = ((EntityUid Uid, MetabolizerComponent Component)[])
                    snapshot.GetType().GetField("_items", flags)!.GetValue(snapshot)!;
                Assert.That(array.All(entry => entry.Component == null), Is.True, "The retained backing array must not pin removed organs.");
                Assert.That(typeof(MetabolizerSystem).GetField("_metabolizerSnapshotInUse", flags)!.GetValue(system), Is.False);
            }

            try
            {
                CreateMetabolizer();
                CreateMetabolizer();
                var initial = fixtures.ToArray();
                var calls = new List<EntityUid>();
                var nested = false;
                effect.Callback = args =>
                {
                    calls.Add(args.TargetEntity);
                    if (nested) return;
                    nested = true;
                    // Native effects may alter the registry while the outer pass is running.
                    var other = initial.Single(entry => entry.Uid != args.TargetEntity);
                    em.RemoveComponent<MetabolizerComponent>(other.Uid);
                    CreateMetabolizer();
                    system.Update(0);
                };
                system.Update(0);
                Assert.That(calls, Is.EquivalentTo(fixtures.Select(entry => entry.Uid)));
                Assert.That(fixtures.All(entry => entry.Solution.Volume == FixedPoint2.New(9)), Is.True);
                AssertReleased();

                effect.Callback = _ => throw new InvalidOperationException("expected metabolism fixture failure");
                foreach (var entry in fixtures.Where(entry => em.HasComponent<MetabolizerComponent>(entry.Uid)))
                    SetDue(entry.Metabolizer);
                Assert.Throws<InvalidOperationException>(() => system.Update(0));
                AssertReleased();

                calls.Clear();
                effect.Callback = args => calls.Add(args.TargetEntity);
                var remaining = fixtures.Where(entry => em.HasComponent<MetabolizerComponent>(entry.Uid)).ToArray();
                foreach (var entry in remaining) SetDue(entry.Metabolizer);
                system.Update(0);
                Assert.That(calls, Is.EquivalentTo(remaining.Select(entry => entry.Uid)));
                Assert.That(remaining.All(entry => entry.Solution.Volume == FixedPoint2.New(8)), Is.True);
                AssertReleased();
            }
            finally
            {
                reagent.Metabolisms = previous;
                em.DeleteEntity(map);
            }
        });
        await pair.CleanReturnAsync();
    }
}
