using System.Linq;
using System.Numerics;
using Content.Server._Shitmed.Medical.Surgery;
using Content.Server.Body.Systems;
using Content.Shared._Shitmed.Medical.Surgery;
using Content.Shared._Shitmed.Medical.Surgery.Conditions;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Standing;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class AriralSurgeryTests
{
    [TestCase("Heart", "heart")]
    [TestCase("Liver", "liver")]
    public async Task NativeOrganCanBeRemovedAndReimplanted(string organType, string slot)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        EntityUid patient = default, surgeon = default, torso = default, organ = default;
        BaseContainer container = null;
        var remove = new EntProtoId("SurgeryRemove" + organType);
        var insert = new EntProtoId("SurgeryInsert" + organType);

        await pair.Server.WaitAssertion(() =>
        {
            var surgeonCoords = map.GridCoords.Offset(Vector2.UnitX);
            entities.System<SharedMapSystem>().SetTile(map.Grid, map.Grid.Comp, surgeonCoords, map.Tile.Tile);
            patient = entities.SpawnEntity("MobAriral", map.GridCoords);
            surgeon = entities.SpawnEntity("MobHuman", surgeonCoords);
            entities.System<StandingStateSystem>().Down(patient, playSound: false);
            entities.AddComponent<SurgerySpeedModifierComponent>(surgeon).SpeedModifier = 30;

            torso = entities.System<BodySystem>().GetBodyChildrenOfType(patient, BodyPartType.Torso).Single().Id;
            Assert.That(entities.System<SharedContainerSystem>().TryGetContainer(
                torso, SharedBodySystem.GetOrganContainerId(slot), out container), Is.True);
            organ = container.ContainedEntities.Single();
            Assert.That(entities.GetComponent<MetaDataComponent>(organ).EntityPrototype?.ID, Is.EqualTo("OrganAriral" + organType));
            Assert.That(IsOffered(remove), Is.True);
            Assert.That(IsOffered(insert), Is.False);
        });

        await PerformStep("SurgeryOpenIncision", "SurgeryStepOpenIncisionScalpel", "Scalpel");
        await PerformStep("SurgeryOpenIncision", "SurgeryStepRetractSkin", "Retractor");
        await PerformStep("SurgeryOpenIncision", "SurgeryStepClampBleeders", "Hemostat");
        await PerformStep("SurgeryOpenRibcage", "SurgeryStepSawBones", "SawElectric");
        await PerformStep("SurgeryOpenRibcage", "SurgeryStepPriseOpenBones", "Retractor");
        await PerformStep(remove, "SurgeryStepClampInternalBleeders", "Hemostat");
        await PerformStep(remove, "SurgeryStepRemoveOrgan", "Hemostat");

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(container.ContainedEntities, Is.Empty);
            Assert.That(entities.GetComponent<OrganComponent>(organ).Body, Is.Null);
            Assert.That(entities.System<SharedHandsSystem>().IsHolding(surgeon, organ), Is.True);
            Assert.That(IsOffered(insert), Is.True);
        });

        await PerformStep(insert, "SurgeryStepInsert" + organType);
        await PerformStep(insert, "SurgeryStepSealOrganWound", "Cautery");

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(container.ContainedEntities.Single(), Is.EqualTo(organ));
            Assert.That(entities.GetComponent<OrganComponent>(organ).Body, Is.EqualTo(patient));
            Assert.That(entities.System<SharedHandsSystem>().IsHolding(surgeon, organ), Is.False);
            Assert.That(IsOffered(insert), Is.False);
            entities.DeleteEntity(surgeon);
            entities.DeleteEntity(patient);
        });
        await pair.CleanReturnAsync();

        bool IsOffered(EntProtoId procedure)
        {
            var surgery = entities.System<SurgerySystem>().GetSingleton(procedure).Value;
            var valid = new SurgeryValidEvent(patient, torso);
            entities.EventBus.RaiseLocalEvent(surgery, ref valid);
            return !valid.Cancelled;
        }

        async Task PerformStep(EntProtoId procedure, EntProtoId step, string toolPrototype = null)
        {
            EntityUid? tool = null;
            await pair.Server.WaitAssertion(() =>
            {
                if (toolPrototype != null)
                {
                    tool = entities.SpawnEntity(toolPrototype, map.GridCoords);
                    Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(surgeon, tool.Value), Is.True);
                }

                var message = new SurgeryStepChosenBuiMsg(entities.GetNetEntity(torso), procedure, step, false)
                {
                    Actor = surgeon,
                    Entity = entities.GetNetEntity(patient),
                    UiKey = SurgeryUIKey.Key,
                };
                entities.EventBus.RaiseLocalEvent(patient, message);
            });
            await pair.Server.WaitRunTicks(5);
            await pair.Server.WaitAssertion(() =>
            {
                var surgery = entities.System<SurgerySystem>();
                Assert.That(surgery.IsStepComplete(patient, torso, step, surgery.GetSingleton(procedure).Value), Is.True, step.Id);
                if (tool is { } usedTool)
                    entities.DeleteEntity(usedTool);
            });
        }
    }
}
