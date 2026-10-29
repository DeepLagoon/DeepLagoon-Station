using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._Goobstation.Tools;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared._Goobstation.Tools;
using Content.Shared._Goobstation.Tools.Components;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions.Events;
using Content.Shared.Burial.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Paper;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Exceptions;
using Robust.Shared.Map;
using Robust.Shared.Localization;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.DoAfter;

[NonParallelizable]
public sealed class DoAfterLifecycleTests : InteractionTest
{
    [TestPrototypes]
    private const string LifecyclePrototypes = @"
- type: entity
  parent: WallSolid
  id: LifecycleRepairWall
  components:
  - type: ShipRepairable
    repairTime: 1
    repairCost: 0
";

    private void SetInstantMode(string mode, EntityUid user)
    {
        if (mode == "tag")
            SEntMan.System<TagSystem>().AddTag(user, "InstantDoAfters");
    }

    private Task EnvelopeVerb(string key) => Server.WaitPost(() =>
    {
        var verb = SEntMan.System<Content.Server.Verbs.VerbSystem>()
            .GetLocalVerbs(STarget!.Value, SPlayer, typeof(AlternativeVerb))
            .Single(verb => verb.Text == Loc.GetString(key));
        verb.Act!.Invoke();
    });

    [TestCase("normal")]
    [TestCase("tag")]
    [TestCase("zero")]
    public async Task EnvelopeCanBeSealedAndTorn(string mode)
    {
        await SpawnTarget("Envelope");
        await Server.WaitPost(() =>
        {
            SetInstantMode(mode, SPlayer);
            var comp = Comp<EnvelopeComponent>();
            comp.SealDelay = comp.TearDelay = mode == "zero" ? TimeSpan.Zero : TimeSpan.FromSeconds(1);
        });
        await EnvelopeVerb("envelope-verb-seal");
        if (mode == "normal")
        {
            var index = DoAfters.NextId;
            await EnvelopeVerb("envelope-verb-seal");
            Assert.That(DoAfters.NextId, Is.EqualTo(index), "An active action must still block duplicates.");
        }
        await AwaitDoAfters();
        await Server.WaitAssertion(() =>
        {
            Assert.That(Comp<EnvelopeComponent>().State, Is.EqualTo(EnvelopeComponent.EnvelopeState.Sealed));
            Assert.That(Comp<EnvelopeComponent>().EnvelopeDoAfter, Is.Null);
        });
        await EnvelopeVerb("envelope-verb-tear");
        await AwaitDoAfters();
        await Server.WaitAssertion(() =>
        {
            Assert.That(Comp<EnvelopeComponent>().State, Is.EqualTo(EnvelopeComponent.EnvelopeState.Torn));
            Assert.That(Comp<EnvelopeComponent>().EnvelopeDoAfter, Is.Null);
        });
    }

    [Test]
    public async Task CancelledEnvelopeCanBeRetried()
    {
        await SpawnTarget("Envelope");
        await EnvelopeVerb("envelope-verb-seal");
        await CancelDoAfters();
        Assert.That(Comp<EnvelopeComponent>().State, Is.EqualTo(EnvelopeComponent.EnvelopeState.Open));
        await EnvelopeVerb("envelope-verb-seal");
        await AwaitDoAfters();
        Assert.That(Comp<EnvelopeComponent>().State, Is.EqualTo(EnvelopeComponent.EnvelopeState.Sealed));
    }

    [TestCase("normal")]
    [TestCase("tag")]
    [TestCase("zero")]
    public async Task GraveCanBeDugRepeatedly(string mode)
    {
        await SpawnTarget("CrateWoodenGrave");
        await Server.WaitPost(() =>
        {
            SetInstantMode(mode, SPlayer);
            Comp<GraveComponent>().DigDelay = mode == "zero" ? TimeSpan.Zero : TimeSpan.FromSeconds(1);
        });
        await PlaceInHands("Shovel");
        for (var i = 0; i < 2; i++)
        {
            var index = DoAfters.NextId;
            await Interact();
            await Server.WaitAssertion(() =>
            {
                Assert.That(DoAfters.NextId, Is.GreaterThan(index), "Each click must start a new digging action.");
                Assert.That(Comp<GraveComponent>().ActiveShovelDigging, Is.False);
                Assert.That(Comp<GraveComponent>().Stream, Is.Null);
            });
        }
    }

    [Test]
    public async Task CancelledDiggingStopsSoundAndCanBeRetried()
    {
        await SpawnTarget("CrateWoodenGrave");
        await InteractUsing("Shovel", awaitDoAfters: false);
        await CancelDoAfters();
        await Server.WaitAssertion(() =>
        {
            Assert.That(Comp<GraveComponent>().ActiveShovelDigging, Is.False);
            Assert.That(Comp<GraveComponent>().Stream, Is.Null);
        });
        await Interact();
        Assert.That(Comp<GraveComponent>().ActiveShovelDigging, Is.False);
    }

    [TestCase("assay", "normal")]
    [TestCase("assay", "tag")]
    [TestCase("assay", "zero")]
    [TestCase("assay", "amplified")]
    [TestCase("heal", "normal")]
    [TestCase("heal", "tag")]
    [TestCase("heal", "zero")]
    [TestCase("regeneration", "normal")]
    [TestCase("regeneration", "tag")]
    [TestCase("regeneration", "zero")]
    public async Task PsionicActionsReleaseTheirCastingState(string power, string mode)
    {
        await SpawnTarget("Paper");
        EntityUid caster = default;
        await Server.WaitPost(() =>
        {
            caster = SEntMan.SpawnEntity("MobHuman", SEntMan.GetCoordinates(PlayerCoords));
            var psionic = SEntMan.EnsureComponent<PsionicComponent>(caster);
            psionic.CurrentAmplification = mode == "amplified" ? 2 : 0;
            SetInstantMode(mode, caster);
            SEntMan.EnsureComponent<PsionicRegenerationPowerComponent>(caster).UseDelay = mode == "zero" ? 0 : 1;
        });
        for (var i = 0; i < 2; i++)
        {
            await Server.WaitAssertion(() =>
            {
                if (power == "regeneration" && mode != "normal")
                {
                    var solutions = SEntMan.System<SharedSolutionContainerSystem>();
                    Assert.That(solutions.TryGetSolution(caster, "chemicals", out _, out var chemicals), Is.True);
                    var before = chemicals!.GetTotalPrototypeQuantity("PsionicRegenerationEssence");
                    CastPower(caster, power, mode);
                    Assert.That(chemicals.GetTotalPrototypeQuantity("PsionicRegenerationEssence") - before,
                        Is.EqualTo(FixedPoint2.New(20)), "A successful instant cast must give the full dose.");
                }
                else
                    CastPower(caster, power, mode);
            });
            await RunSeconds(2);
            await Server.WaitAssertion(() =>
            {
                Assert.That(SEntMan.GetComponent<PsionicComponent>(caster).DoAfter, Is.Null);
                Assert.That(SEntMan.GetComponent<PsionicRegenerationPowerComponent>(caster).DoAfter, Is.Null);
                Assert.That(SEntMan.System<SharedPsionicAbilitiesSystem>().OnAttemptPowerUse(caster, "test", false), Is.True);
            });
        }
    }

    [TestCase("assay")]
    [TestCase("heal")]
    [TestCase("regeneration")]
    public async Task CancelledPsionicActionsCanBeRetried(string power)
    {
        await SpawnTarget("Paper");
        EntityUid caster = default;
        await Server.WaitAssertion(() =>
        {
            caster = SEntMan.SpawnEntity("MobHuman", SEntMan.GetCoordinates(PlayerCoords));
            SEntMan.EnsureComponent<PsionicComponent>(caster).CurrentAmplification = 0;
            SEntMan.EnsureComponent<PsionicRegenerationPowerComponent>(caster).UseDelay = 1;
            CastPower(caster, power, "normal");
            var id = power == "regeneration"
                ? SEntMan.GetComponent<PsionicRegenerationPowerComponent>(caster).DoAfter
                : SEntMan.GetComponent<PsionicComponent>(caster).DoAfter;
            Assert.That(DoAfterSys.IsRunning(id), Is.True);
            DoAfterSys.Cancel(id);
            Assert.That(SEntMan.GetComponent<PsionicComponent>(caster).DoAfter, Is.Null);
            Assert.That(SEntMan.GetComponent<PsionicRegenerationPowerComponent>(caster).DoAfter, Is.Null);
        });
        await RunSeconds(1);
        await Server.WaitAssertion(() => CastPower(caster, power, "normal"));
        await RunSeconds(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<PsionicComponent>(caster).DoAfter, Is.Null);
            Assert.That(SEntMan.GetComponent<PsionicRegenerationPowerComponent>(caster).DoAfter, Is.Null);
        });
    }

    [Test]
    public async Task CancelledRegenerationOnlyGivesPartialDose()
    {
        await SpawnTarget("Paper");
        EntityUid caster = default;
        await Server.WaitPost(() =>
        {
            caster = SEntMan.SpawnEntity("MobHuman", SEntMan.GetCoordinates(PlayerCoords));
            SEntMan.EnsureComponent<PsionicComponent>(caster).CurrentAmplification = 0;
            SEntMan.EnsureComponent<PsionicRegenerationPowerComponent>(caster).UseDelay = 1;
            CastPower(caster, "regeneration", "normal");
        });
        await RunSeconds(0.25f);
        await Server.WaitAssertion(() =>
        {
            var id = SEntMan.GetComponent<PsionicRegenerationPowerComponent>(caster).DoAfter;
            Assert.That(DoAfterSys.IsRunning(id), Is.True);
            DoAfterSys.Cancel(id);
            Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(caster, "chemicals", out _, out var chemicals), Is.True);
            var dose = chemicals!.GetTotalPrototypeQuantity("PsionicRegenerationEssence");
            Assert.That(dose, Is.GreaterThan(FixedPoint2.Zero));
            Assert.That(dose, Is.LessThan(FixedPoint2.New(20)));
        });
    }

    private void CastPower(EntityUid caster, string power, string mode)
    {
        switch (power)
        {
            case "assay":
                var assay = new AssayPowerActionEvent
                {
                    Performer = caster, Target = STarget!.Value,
                    UseDelay = mode == "zero" ? TimeSpan.Zero : TimeSpan.FromSeconds(1)
                };
                SEntMan.EventBus.RaiseLocalEvent(caster, assay);
                Assert.That(assay.Handled, Is.True);
                break;
            case "heal":
                var heal = new PsionicHealOtherPowerActionEvent
                {
                    Performer = caster, Target = caster, PowerName = "test healing",
                    UseDelay = mode == "zero" ? 0 : 1
                };
                SEntMan.EventBus.RaiseLocalEvent(caster, heal);
                Assert.That(heal.Handled, Is.True);
                break;
            case "regeneration":
                var regeneration = new PsionicRegenerationPowerActionEvent { Performer = caster };
                SEntMan.EventBus.RaiseLocalEvent(caster, regeneration);
                Assert.That(regeneration.Handled, Is.True);
                break;
        }
    }

    [TestCase("normal")]
    [TestCase("tag")]
    [TestCase("zero")]
    public async Task TileRepairsDoNotAccumulateCompletedIds(string mode)
    {
        await PlaceInHands("ShipRepairDevice");
        var tool = Hands.ActiveHandEntity!.Value;
        await Server.WaitPost(() =>
        {
            SetInstantMode(mode, SPlayer);
            var comp = SEntMan.GetComponent<ShipRepairToolComponent>(tool);
            comp.TileRepairTime = mode == "zero" ? 0 : 1;
            comp.TileRepairCost = 0;
            comp.DoAfters.Add(new DoAfterId(SPlayer, ushort.MaxValue));
            SEntMan.System<SharedShipRepairSystem>().GenerateRepairData(MapData.Grid);
        });
        for (var i = 0; i < 3; i++)
        {
            await SetTile("Space");
            await Interact();
            await AssertTile(Plating);
            await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<ShipRepairToolComponent>(tool).DoAfters, Is.Empty));
        }
    }

    [Test]
    public async Task RepairIdsBelongToTheirOriginalUser()
    {
        await SpawnTarget("LifecycleRepairWall");
        await PlaceInHands("ShipRepairDevice");
        var tool = Hands.ActiveHandEntity!.Value;
        DoAfterId? otherId = null;
        DoAfterId? ownId = null;
        await Server.WaitPost(() =>
        {
            var repair = SEntMan.System<SharedShipRepairSystem>();
            repair.GenerateRepairData(MapData.Grid);
            var data = SEntMan.GetComponent<ShipRepairDataComponent>(MapData.Grid);
            var original = SEntMan.GetNetEntity(STarget!.Value);
            var spec = data.Chunks.Values.SelectMany(chunk => chunk.Entities).Single(kv => kv.Value.OriginalEntity == original);
            var target = STarget.Value;
            Target = null;
            SEntMan.DeleteEntity(target);
            var other = SEntMan.SpawnEntity("InteractionTestMob", SEntMan.GetCoordinates(PlayerCoords));
            var ev = new ShipRepairDoAfterEvent
            {
                TargetGrid = MapData.Grid, TargetGridIndices = new Vector2i(1, 0), RepairId = spec.Key, Cost = 0
            };
            Assert.That(DoAfterSys.TryStartDoAfter(new DoAfterArgs(SEntMan, other, 30, ev, tool), out otherId), Is.True);
            SEntMan.GetComponent<ShipRepairToolComponent>(tool).DoAfters.Add(otherId!.Value);
            Assert.That(DoAfterSys.TryStartDoAfter(new DoAfterArgs(SEntMan, SPlayer, 30, new EnvelopeDoAfterEvent(), SPlayer), out ownId), Is.True);
            Assert.That(ownId!.Value.Index, Is.EqualTo(otherId.Value.Index));
        });
        await Interact(awaitDoAfters: false);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<ShipRepairToolComponent>(tool).DoAfters, Is.EqualTo(new[] { otherId!.Value }));
            Assert.That(DoAfterSys.IsRunning(otherId), Is.True);
            Assert.That(DoAfters.NextId, Is.EqualTo(ownId!.Value.Index + 1), "The existing repair must block a second repair of the same entity.");
            DoAfterSys.Cancel(otherId);
            DoAfterSys.Cancel(ownId);
        });
        await Server.WaitPost(() => SEntMan.DeleteEntity(MapData.Grid));
        await RunTicks(5);
        await Client.WaitAssertion(() =>
        {
            var runtime = Client.ResolveDependency<IRuntimeLog>();
            Assert.That(runtime.ExceptionCount, Is.Zero, runtime.Display());
        });
    }

    [Test]
    public async Task RepairsOnDifferentGridsAreNotDuplicates()
    {
        await Server.WaitAssertion(() =>
        {
            var otherGrid = MapMan.CreateGridEntity(MapId);
            var first = new ShipRepairDoAfterEvent { TargetGrid = MapData.Grid, TargetGridIndices = new Vector2i(1, 2), RepairId = 3 };
            var second = new ShipRepairDoAfterEvent { TargetGrid = otherGrid, TargetGridIndices = new Vector2i(1, 2), RepairId = 3 };
            var replicated = new ShipRepairDoAfterEvent { TargetGridIndices = first.TargetGridIndices, RepairId = first.RepairId };
            Assert.That(first.IsDuplicate(replicated), Is.True, "Replicated events without a local grid reference must still block duplicates.");
            Assert.That(DoAfterSys.TryStartDoAfter(new DoAfterArgs(SEntMan, SPlayer, 30, first, SPlayer), out var firstId), Is.True);
            Assert.That(DoAfterSys.TryStartDoAfter(new DoAfterArgs(SEntMan, SPlayer, 30, second, SPlayer), out var secondId), Is.True);
            Assert.That(DoAfterSys.IsRunning(firstId), Is.True);
            Assert.That(DoAfterSys.IsRunning(secondId), Is.True);
            DoAfterSys.Cancel(firstId);
            DoAfterSys.Cancel(secondId);
        });
    }

    [Test]
    public async Task DuplicateSparkEventsUseTheSparkAnimationPlayer()
    {
        await SpawnTarget("LockerFreezer");
        NetEntity sparks = default;
        await Server.WaitPost(() => sparks = SEntMan.GetNetEntity(SEntMan.SpawnEntity("EffectWeldingSparks", SEntMan.GetCoordinates(TargetCoords))));
        await RunTicks(5);
        await Client.WaitAssertion(() =>
        {
            CEntMan.EnsureComponent<WeldingSparksAnimationComponent>(CTarget!.Value).StartingOffset = new Vector2(0.5f, 0);
            var hadTargetPlayer = CEntMan.HasComponent<AnimationPlayerComponent>(CTarget.Value);
            var system = CEntMan.System<WeldingSparksAnimationSystem>();
            var handler = typeof(WeldingSparksAnimationSystem).GetMethod("OnSpawnedWeldingSparks", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ev = new SpawnedWeldingSparksEvent(Target!.Value, sparks, TimeSpan.FromSeconds(2));
            handler.Invoke(system, new object[] { ev });
            handler.Invoke(system, new object[] { ev });
            Assert.That(CEntMan.System<AnimationPlayerSystem>().HasRunningAnimation(CEntMan.GetEntity(sparks), "WeldAnim"), Is.True);
            Assert.That(CEntMan.HasComponent<AnimationPlayerComponent>(CTarget.Value), Is.EqualTo(hadTargetPlayer));
        });
    }
    [Test]
    public async Task ForcedParentPreservesPositionButAllowsNullspaceDetach()
    {
        NetEntity gridNet = default;
        await Server.WaitPost(() => gridNet = SEntMan.GetNetEntity(MapData.Grid));
        await Client.WaitAssertion(() =>
        {
            var grid = CEntMan.GetEntity(gridNet);
            var map = CEntMan.GetComponent<TransformComponent>(grid).MapUid!.Value;
            var transform = CEntMan.System<SharedTransformSystem>();
            var parent = CEntMan.System<Content.Shared._Mono.ForceParent.ForceParentSystem>();
            var position = new Vector2(0.5f, 0.5f);
            var ghost = CEntMan.SpawnEntity(null, new EntityCoordinates(grid, position));
            parent.SetForceParent(ghost, new EntityCoordinates(grid, position));
            transform.SetCoordinates(ghost, new EntityCoordinates(map, new Vector2(3, 3)));
            var comp = CEntMan.GetComponent<TransformComponent>(ghost);
            Assert.That(comp.ParentUid, Is.EqualTo(grid));
            Assert.That(comp.LocalPosition, Is.EqualTo(position));
            transform.DetachEntity(ghost);
            Assert.That(comp.ParentUid.Valid, Is.False);
            transform.SetCoordinates(ghost, new EntityCoordinates(map, new Vector2(3, 3)));
            Assert.That(comp.ParentUid, Is.EqualTo(grid));
            Assert.That(comp.LocalPosition, Is.EqualTo(position));
            CEntMan.DeleteEntity(ghost);
        });
    }

}
