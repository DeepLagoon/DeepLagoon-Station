using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Server._DeepLagoon.LustWish;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BlueMoonProtectionTests
{
    private static void Gear(IEntityManager entities, ICommonSession session, EntityUid target, string operation) =>
        typeof(LustWishEquipmentSystem).GetMethod("OnRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(entities.System<LustWishEquipmentSystem>(), [new LustWishGearRequestEvent(entities.GetNetEntity(target), operation, "penis"), new EntitySessionEventArgs(session)]);

    private static void Climax(IEntityManager entities, ICommonSession session, EntityUid target, string part = "penis") =>
        typeof(InteractionPanelSystem).GetMethod("OnClimaxRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(entities.System<InteractionPanelSystem>(), [new InteractionPanelClimaxRequestEvent(entities.GetNetEntity(target), part, "surface", container: true), new EntitySessionEventArgs(session)]);

    private static void Anatomy(IEntityManager entities, EntityUid uid, int potency = -1)
    {
        entities.GetComponent<HumanoidAppearanceComponent>(uid).BodyFeatures = new[] { "penis", "testicles" }.Select(part =>
        {
            var feature = BodyCustomizationCatalog.Default(part);
            feature.Enabled = true;
            return feature;
        }).ToList();
        var prefs = entities.GetComponent<InteractionPanelPreferencesComponent>(uid);
        prefs.Profile = prefs.Profile.WithArousalPreferences(new BlueMoonPreferences { Potency = potency });
    }

    [Test]
    public async Task ProtectionMustBeUnwrappedCapturesFluidsAndCageBlocksStimulation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(1);
        EntityUid user = default, condom = default, cage = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            user = e.SpawnEntity("MobHuman", map.GridCoords);
            Anatomy(e, user);
            pair.Server.PlayerMan.SetStatus(sessions[0], Robust.Shared.Enums.SessionStatus.InGame);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], user);
            e.System<InteractionPanelSystem>().OpenPanel(user, user);
            condom = e.SpawnEntity("DLBlueMoonCondom", map.GridCoords);
            Assert.That(e.System<SharedHandsSystem>().TryPickupAnyHand(user, condom), Is.True);
            Gear(e, sessions[0], user, "attach");
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(condom).Wearer, Is.Null, "Wrapped protection cannot be attached");
            e.EventBus.RaiseLocalEvent(condom, new UseInHandEvent(user));
            Gear(e, sessions[0], user, "attach");
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(condom).Wearer, Is.Null, "Attachment is delayed");
        });
        await pair.RunTicksSync(pair.SecondsToTicks(5));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(condom).Wearer, Is.EqualTo(user));
            var arousal = e.System<InteractionPanelArousalSystem>();
            e.GetComponent<BlueMoonPhysiologyComponent>(user).LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(250);
            arousal.TryClimax(user, bodyPart: "penis", allParts: false);
            Assert.That(e.System<SharedSolutionContainerSystem>().TryGetSolution(condom, "captured", out _, out var contents), Is.True);
            Assert.That(contents!.Volume, Is.EqualTo(FixedPoint2.New(50)), "Protection retains the emitted fluid");
            Gear(e, sessions[0], user, "remove");
            var hands = e.System<SharedHandsSystem>();
            foreach (var hand in hands.EnumerateHands(user).ToArray())
                if (hand.HeldEntity != null) hands.TryDrop(user, hand, checkActionBlocker: false);
            cage = e.SpawnEntity("DLBlueMoonChastityCage", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, cage), Is.True);
            Gear(e, sessions[0], user, "attach");
        });
        await pair.RunTicksSync(pair.SecondsToTicks(5));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(cage).Wearer, Is.EqualTo(user));
            var system = e.System<InteractionPanelArousalSystem>();
            Assert.That(system.CanExposePart(user, "penis"), Is.False);
            var state = e.GetComponent<InteractionPanelArousalComponent>(user);
            state.Current = 0;
            system.AdjustArousal(user, user, 12, bodyPart: "penis");
            Assert.That(state.Current, Is.Zero);
            var count = state.Orgasms;
            Climax(e, sessions[0], user);
            Assert.That(state.Orgasms, Is.EqualTo(count));
            Gear(e, sessions[0], user, "remove");
            Assert.That(system.CanExposePart(user, "penis"), Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], null);
            e.DeleteEntity(map.MapUid);
        });
        await pair.Server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManualEmissionRevalidatesPanelAndCancelsWhenTheHeldVesselIsDropped()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(1);
        EntityUid user = default, vessel = default, other = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            user = e.SpawnEntity("MobHuman", map.GridCoords);
            other = e.SpawnEntity("MobHuman", map.GridCoords);
            Anatomy(e, user, potency: 1);
            pair.Server.PlayerMan.SetStatus(sessions[0], Robust.Shared.Enums.SessionStatus.InGame);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], user);
            e.System<InteractionPanelSystem>().OpenPanel(user, user);
            vessel = e.SpawnEntity("Beaker", map.GridCoords);
            var hands = e.System<SharedHandsSystem>();
            Assert.That(hands.TryPickupAnyHand(user, vessel), Is.True);
            e.GetComponent<BlueMoonPhysiologyComponent>(user).LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(250);
            Climax(e, sessions[0], other);
            Climax(e, sessions[0], user, "invalid");
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Orgasms, Is.EqualTo(1));
            Climax(e, sessions[0], user);
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Orgasms, Is.EqualTo(1), "Manual emission takes three seconds");
            Assert.That(hands.TryDrop(user, vessel), Is.True);
        });
        await pair.RunTicksSync(pair.SecondsToTicks(4));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Orgasms, Is.EqualTo(1));
            Assert.That(e.System<SharedHandsSystem>().TryPickupAnyHand(user, vessel), Is.True);
            Climax(e, sessions[0], user);
        });
        await pair.RunTicksSync(pair.SecondsToTicks(4));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Orgasms, Is.EqualTo(1),
                "Manual release does not spend the source automatic-cycle counter");
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).RecoveryUntil, Is.EqualTo(TimeSpan.Zero));
            Assert.That(e.System<SharedSolutionContainerSystem>().TryGetRefillableSolution(vessel, out _, out var contents), Is.True);
            Assert.That(contents!.GetTotalPrototypeQuantity("DLSemen"), Is.EqualTo(FixedPoint2.New(50)));
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], null);
            e.DeleteEntity(map.MapUid);
        });
        await pair.Server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();
    }
}
