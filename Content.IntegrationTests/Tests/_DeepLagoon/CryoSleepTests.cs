using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Client._NF.CryoSleep;
using Content.Server.Body.Systems;
using Content.Server.GameTicking;
using Content.Server.Ghost;
using Content.Server.Mind;
using Content.Shared._NF.CCVar;
using Content.Shared.Alert;
using Content.Shared.GameTicking;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Preferences;
using Content.Shared.StatusEffect;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Server.Containers;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using ServerCryoSystem = Content.Server._NF.CryoSleep.CryoSleepSystem;
using Content.Server._NF.CryoSleep;
using static Content.Shared._NF.CryoSleep.SharedCryoSleepSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class CryoSleepTests
{
    [TestCase("close")]
    [TestCase("deny")]
    [TestCase("accept")]
    public async Task ConfirmationChoiceIsSentOnceAndClosingEjects(string choice)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        EntityUid body = default;
        EntityUid pod = default;
        await pair.Server.WaitAssertion(() =>
        {
            body = entities.SpawnEntity("MobHuman", map.GridCoords);
            pod = entities.SpawnEntity("MachineCryoSleepPod", map.GridCoords);
            var minds = entities.System<MindSystem>();
            var mind = minds.GetOrCreateMind(pair.Server.PlayerMan.Sessions.Single().UserId);
            minds.TransferTo(mind.Owner, body);
            Assert.That(entities.System<ServerCryoSystem>().InsertBody(body,
                entities.GetComponent<CryoSleepComponent>(pod), false), Is.True);
        });
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var window = Descendants(pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot)
                .OfType<AcceptCryoWindow>().Single();
            if (choice == "close")
            {
                window.Close();
                return;
            }

            var button = choice == "accept" ? window.AcceptButton : window.DenyButton;
            var pressed = (Action<BaseButton.ButtonEventArgs>) typeof(BaseButton)
                .GetField("OnPressed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button)!;
            pressed(new BaseButton.ButtonEventArgs(button, null!));
        });
        await pair.RunTicksSync(20);
        await pair.Server.WaitAssertion(() =>
        {
            var cryo = entities.System<ServerCryoSystem>();
            Assert.That(entities.GetComponent<CryoSleepComponent>(pod).BodyContainer.ContainedEntity, Is.Null);
            Assert.That(cryo.HasCryosleepingBody(pair.Server.PlayerMan.Sessions.Single().UserId),
                Is.EqualTo(choice == "accept"));
            Assert.That(entities.GetComponent<MetaDataComponent>(body).EntityPaused, Is.EqualTo(choice == "accept"));
        });
        // In particular, closing must cancel the pending 30-second automatic storage.
        if (choice != "accept")
        {
            await pair.RunTicksSync(1900);
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(entities.System<ServerCryoSystem>().HasCryosleepingBody(
                    pair.Server.PlayerMan.Sessions.Single().UserId), Is.False);
                Assert.That(entities.GetComponent<TransformComponent>(body).MapUid, Is.EqualTo(map.MapUid));
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StoredBodyKeepsNestedEquipmentAcrossExpiryLobbyRespawnAndRepeatedVisits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        EntityUid body = default;
        EntityUid pod = default;
        EntityUid backpack = default;
        EntityUid item = default;
        float hungerBefore = default;
        float thirstBefore = default;
        float bloodBefore = default;
        var timing = pair.Server.ResolveDependency<IGameTiming>();
        var tickRate = timing.TickRate;
        var effectDuration = TimeSpan.FromSeconds(2.5);
        const string statusEffect = "Muted";
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(NFCCVars.CryoExpirationTime, 0.1f);
            pair.Server.CfgMan.SetCVar(NFCCVars.CryoReturnEnabled, true);
            body = entities.SpawnEntity("MobHuman", map.GridCoords);
            pod = entities.SpawnEntity("MachineCryoSleepPod", map.GridCoords);
            backpack = entities.SpawnEntity("ClothingBackpack", map.GridCoords);
            item = entities.SpawnEntity(null, map.GridCoords);
            Assert.That(entities.System<InventorySystem>().TryEquip(body, backpack, "back", force: true), Is.True);
            var containers = entities.System<ContainerSystem>();
            Assert.That(containers.Insert(item, containers.EnsureContainer<ContainerSlot>(backpack, "cryo-test")), Is.True);
            var minds = entities.System<MindSystem>();
            minds.TransferTo(minds.GetOrCreateMind(pair.Server.PlayerMan.Sessions.Single().UserId).Owner, body);
        });

        for (var cycle = 0; cycle < 2; cycle++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                var cryo = entities.System<ServerCryoSystem>();
                var blood = entities.System<BloodstreamSystem>();
                var hunger = entities.System<HungerSystem>();
                hunger.SetHunger(body, 120f);
                hungerBefore = hunger.GetHunger(entities.GetComponent<HungerComponent>(body));
                thirstBefore = entities.GetComponent<ThirstComponent>(body).CurrentThirst;
                Assert.That(blood.TryModifyBleedAmount(body, 3f), Is.True);
                bloodBefore = blood.GetBloodLevelPercentage(body);
                Assert.That(entities.System<StatusEffectsSystem>().TryAddStatusEffect(body, statusEffect,
                    effectDuration, true), Is.True);
                Assert.That(cryo.InsertBody(body, entities.GetComponent<CryoSleepComponent>(pod), false), Is.True);
                cryo.AcceptCryoSleep(body, pod);
                Assert.That(entities.GetComponent<MetaDataComponent>(body).EntityPaused, Is.True);
                Assert.That(entities.GetComponent<MetaDataComponent>(backpack).EntityPaused, Is.True);
                Assert.That(entities.GetComponent<MetaDataComponent>(item).EntityPaused, Is.True);
                Assert.That(entities.System<BodySystem>().GetBodyOrgans(body), Is.Not.Empty);
                foreach (var (organ, _) in entities.System<BodySystem>().GetBodyOrgans(body))
                    Assert.That(entities.GetComponent<MetaDataComponent>(organ).EntityPaused, Is.True);
                var session = pair.Server.PlayerMan.Sessions.Single();
                entities.EventBus.RaiseEvent(EventSource.Local, new PlayerJoinedLobbyEvent(session));
                entities.EventBus.RaiseEvent(EventSource.Local, new PlayerBeforeSpawnEvent(session,
                    HumanoidCharacterProfile.DefaultWithSpecies("Human"), null, true, map.MapUid));
            });
            await pair.RunTicksSync(4 * tickRate);
            await pair.Server.WaitAssertion(() =>
            {
                var cryo = entities.System<ServerCryoSystem>();
                var session = pair.Server.PlayerMan.Sessions.Single();
                Assert.That(entities.EntityExists(body), Is.True);
                Assert.That(entities.EntityExists(backpack), Is.True);
                Assert.That(entities.EntityExists(item), Is.True);
                Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.True,
                    "Expiration, lobby and respawn must not discard the stored character");
                var mind = entities.System<MindSystem>().GetOrCreateMind(session.UserId).Comp;
                if (cycle == 1)
                {
                    entities.DeleteEntity(pod);
                    pod = entities.SpawnEntity("MachineCryoSleepPodFallback", map.GridCoords);
                }
                // An occupied pod must leave the saved body available for a retry.
                var blocker = entities.SpawnEntity(null, map.GridCoords);
                var container = entities.GetComponent<CryoSleepComponent>(pod).BodyContainer;
                Assert.That(entities.System<ContainerSystem>().Insert(blocker, container), Is.True);
                Assert.That(cryo.TryReturnToBody(mind), Is.EqualTo(cycle == 0 ? ReturnToBodyStatus.Occupied : ReturnToBodyStatus.NoCryopodAvailable));
                Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.True);
                Assert.That(entities.GetComponent<MetaDataComponent>(body).EntityPaused, Is.True);
                Assert.That(entities.System<HungerSystem>().GetHunger(entities.GetComponent<HungerComponent>(body)),
                    Is.EqualTo(hungerBefore));
                Assert.That(entities.GetComponent<ThirstComponent>(body).CurrentThirst, Is.EqualTo(thirstBefore));
                Assert.That(entities.System<BloodstreamSystem>().GetBloodLevelPercentage(body), Is.EqualTo(bloodBefore));
                var effects = entities.System<StatusEffectsSystem>();
                Assert.That(effects.TryGetTime(body, statusEffect, out var cooldown), Is.True);
                Assert.That(cooldown.Value.Item2 - timing.CurTime, Is.EqualTo(effectDuration));
                entities.DeleteEntity(blocker);
                var maps = entities.System<SharedMapSystem>();
                maps.SetPaused(map.MapUid, true);
                Assert.That(cryo.TryReturnToBody(mind), Is.EqualTo(ReturnToBodyStatus.NoCryopodAvailable));
                Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.True);
                maps.SetPaused(map.MapUid, false);
                Assert.That(cryo.TryReturnToBody(mind), Is.EqualTo(ReturnToBodyStatus.Success));
                Assert.That(session.AttachedEntity, Is.EqualTo(body));
                Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.False);
                Assert.That(entities.GetComponent<MetaDataComponent>(body).EntityPaused, Is.False);
                Assert.That(entities.GetComponent<TransformComponent>(body).MapUid, Is.EqualTo(map.MapUid));
                Assert.That(entities.System<HungerSystem>().GetHunger(entities.GetComponent<HungerComponent>(body)),
                    Is.EqualTo(hungerBefore), "Hunger must not catch up when returning from cryosleep.");
                var alerts = entities.System<AlertsSystem>();
                Assert.That(alerts.TryGet(statusEffect, out var alert), Is.True);
                Assert.That(alerts.TryGetAlertState(body, alert.AlertKey, out var state), Is.True);
                Assert.That(state.Cooldown, Is.EqualTo(cooldown));
                foreach (var (organ, _) in entities.System<BodySystem>().GetBodyOrgans(body))
                    Assert.That(entities.GetComponent<MetaDataComponent>(organ).EntityPaused, Is.False);
                Assert.That(entities.GetComponent<TransformComponent>(backpack).ParentUid, Is.EqualTo(body));
                Assert.That(entities.GetComponent<TransformComponent>(item).ParentUid, Is.EqualTo(backpack));
                Assert.That(entities.GetComponent<MetaDataComponent>(item).EntityPaused, Is.False);
                Assert.That(cryo.EjectBody(pod), Is.True);
            });
            await pair.RunTicksSync(2 * tickRate);
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(entities.System<HungerSystem>().GetHunger(entities.GetComponent<HungerComponent>(body)),
                    Is.LessThan(hungerBefore));
                Assert.That(entities.GetComponent<ThirstComponent>(body).CurrentThirst, Is.LessThan(thirstBefore));
                Assert.That(entities.System<StatusEffectsSystem>().HasStatusEffect(body, statusEffect), Is.True);
            });
            await pair.RunTicksSync(2 * tickRate);
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(entities.System<BloodstreamSystem>().GetBloodLevelPercentage(body), Is.LessThan(bloodBefore));
                Assert.That(entities.System<StatusEffectsSystem>().HasStatusEffect(body, statusEffect), Is.False);
            });
        }
        await pair.Server.WaitAssertion(() =>
        {
            var cryo = entities.System<ServerCryoSystem>();
            var minds = entities.System<MindSystem>();
            var session = pair.Server.PlayerMan.Sessions.Single();
            Assert.That(cryo.InsertBody(body, entities.GetComponent<CryoSleepComponent>(pod), false), Is.True);
            cryo.AcceptCryoSleep(body, pod);
            var otherBody = entities.SpawnEntity("MobHuman", map.GridCoords);
            minds.ControlMob(session.UserId, otherBody);
            Assert.That(cryo.InsertBody(otherBody, entities.GetComponent<CryoSleepComponent>(pod), false), Is.True);
            cryo.AcceptCryoSleep(otherBody, pod);
            Assert.That(cryo.TryReturnToBody(minds.GetOrCreateMind(session.UserId).Comp), Is.EqualTo(ReturnToBodyStatus.Success));
            Assert.That(session.AttachedEntity, Is.EqualTo(otherBody));
            Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.True,
                "Storing a second character must not overwrite the first character");
            Assert.That(cryo.EjectBody(pod), Is.True);
            var mind = minds.GetOrCreateMind(session.UserId);
            entities.System<GhostSystem>().OnGhostAttempt(mind.Owner, false, true, mind: mind.Comp);
            Assert.That(cryo.TryReturnToBody(minds.GetOrCreateMind(session.UserId).Comp), Is.EqualTo(ReturnToBodyStatus.Success));
            Assert.That(session.AttachedEntity, Is.EqualTo(body));
            Assert.That(entities.GetComponent<TransformComponent>(item).ParentUid, Is.EqualTo(backpack));
            Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    private static IEnumerable<Control> Descendants(Control root) =>
        root.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
