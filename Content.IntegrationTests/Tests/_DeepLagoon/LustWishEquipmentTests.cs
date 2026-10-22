using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Server._DeepLagoon.LustWish;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.ActionBlocker;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Interaction;
using Content.Shared.Power.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Maths;
using Robust.Shared.IoC;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LustWishEquipmentTests
{
    private static void Request(LustWishEquipmentSystem system, ICommonSession session, EntityUid target,
        string operation, string slot = "", string value = "") =>
        typeof(LustWishEquipmentSystem).GetMethod("OnRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(system, [new LustWishGearRequestEvent(IoCManager.Resolve<IEntityManager>().GetNetEntity(target), operation, slot, value), new EntitySessionEventArgs(session)]);

    private static void Answer(LustWishEquipmentSystem system, ICommonSession session, Guid token) =>
        typeof(LustWishEquipmentSystem).GetMethod("OnAnswer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(system, [new InteractionPanelAnswerEvent(token, true), new EntitySessionEventArgs(session)]);

    private static void Hold(SharedHandsSystem hands, EntityUid user, EntityUid item)
    {
        foreach (var hand in hands.EnumerateHands(user).ToArray())
            if (hand.HeldEntity != null) hands.TryDrop(user, hand, checkActionBlocker: false);
        Assert.That(hands.TryPickupAnyHand(user, item), Is.True);
        Assert.That(hands.IsHolding(user, item, out var held), Is.True);
        hands.SetActiveHand(user, held);
    }

    [Test]
    public async Task ConsentSnapshotsKeysAndRestrictionsAreEnforcedByTheServer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(2);
        EntityUid escapeTarget = default, escapeItem = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var coords = new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f));
            var user = e.SpawnEntity("MobHuman", coords);
            var target = e.SpawnEntity("MobHuman", coords);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], user);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], target);
            var gear = e.System<LustWishEquipmentSystem>();
            var panels = e.System<InteractionPanelSystem>();
            var hands = e.System<SharedHandsSystem>();
            void Consent(EntityUid body, InteractionPanelConsent value)
            {
                var c = e.GetComponent<InteractionPanelPreferencesComponent>(body);
                c.Profile = c.Profile.WithInteractionPanelConsent(InteractionPanelCategory.Erotic, value);
            }
            var collar = e.SpawnEntity("DLLustWishCollar", coords);
            Hold(hands, user, collar);
            var accessory = e.GetComponent<LustWishAccessoryComponent>(collar);
            Request(gear, sessions[0], target, "attach");
            Assert.That(accessory.Wearer, Is.Null, "An unopened panel cannot authorize equipment operations");
            panels.OpenPanel(user, target);
            Consent(user, InteractionPanelConsent.Yes);
            Consent(target, InteractionPanelConsent.No);
            Request(gear, sessions[0], target, "attach");
            Assert.That(accessory.Wearer, Is.Null);
            Consent(target, InteractionPanelConsent.Ask);
            Request(gear, sessions[0], target, "attach");
            var pending = (IDictionary) typeof(LustWishEquipmentSystem).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gear)!;
            Assert.That(pending.Count, Is.EqualTo(1));
            var token = (Guid) pending.Keys.Cast<object>().Single();
            Answer(gear, sessions[0], token);
            Assert.That(accessory.Wearer, Is.Null, "The requesting player cannot accept on behalf of the target");
            hands.TryDrop(user, collar);
            Answer(gear, sessions[1], token);
            Assert.That(accessory.Wearer, Is.Null, "Consent must not survive swapping the held item");
            Consent(target, InteractionPanelConsent.Yes);
            Hold(hands, user, collar);
            Request(gear, sessions[0], target, "attach");
            Assert.That(accessory.Wearer, Is.EqualTo(target));
            var originalKey = e.SpawnEntity("DLLustWishKey", coords);
            Hold(hands, user, originalKey);
            Request(gear, sessions[0], target, "lock", "neck");
            Assert.That(accessory.Locked, Is.True);
            var containers = e.System<SharedContainerSystem>();
            Assert.That(containers.TryGetContainer(target, LustWishEquipment.ContainerPrefix + "neck", out var container), Is.True);
            Assert.That(containers.Remove(collar, container), Is.False, "Generic container removal must not bypass the accessory lock");
            var wrongKey = e.SpawnEntity("DLLustWishKey", coords);
            Hold(hands, user, wrongKey);
            Request(gear, sessions[0], target, "unlock", "neck");
            Assert.That(accessory.Locked, Is.True);
            var copy = e.SpawnEntity("DLLustWishKey", coords);
            Hold(hands, user, originalKey);
            e.EventBus.RaiseLocalEvent(copy, new InteractUsingEvent(user, originalKey, copy, coords));
            Assert.That(e.GetComponent<LustWishKeyComponent>(copy).Code, Is.EqualTo(accessory.KeyCode));
            Hold(hands, user, copy);
            Request(gear, sessions[0], target, "unlock", "neck");
            Assert.That(accessory.Locked, Is.False);
            Request(gear, sessions[0], target, "color", "neck", "#AC70D4");
            Assert.That(e.GetComponent<LustWishPaletteComponent>(collar).Color, Is.EqualTo(Color.FromHex("#AC70D4")));
            Request(gear, sessions[0], target, "color", "neck", "invalid");
            Assert.That(e.GetComponent<LustWishPaletteComponent>(collar).Color, Is.EqualTo(Color.FromHex("#AC70D4")));
            var leash = e.SpawnEntity("DLLustWishLeash", coords);
            Hold(hands, user, leash);
            Request(gear, sessions[0], target, "leash", "neck");
            Assert.That(accessory.LeashHolder, Is.EqualTo(user));
            Assert.That(e.GetComponent<Content.Shared.Movement.Pulling.Components.PullableComponent>(target).Puller, Is.EqualTo(user));
            Request(gear, sessions[0], target, "unleash", "neck");
            Assert.That(accessory.LeashHolder, Is.Null);
            Assert.That(e.GetComponent<Content.Shared.Movement.Pulling.Components.PullableComponent>(target).Puller, Is.Null);
            panels.OpenPanel(target, target);
            Request(gear, sessions[1], target, "release-all");
            var mittens = e.SpawnEntity("DLLustWishMittens", coords);
            Hold(hands, target, mittens);
            Request(gear, sessions[1], target, "attach");
            Assert.That(e.GetComponent<LustWishWornEffectsComponent>(target).HandsBlocked, Is.True);
            Assert.That(e.System<ActionBlockerSystem>().CanInteract(target, target), Is.False);
            Request(gear, sessions[1], target, "release-all");
            Assert.That(e.System<ActionBlockerSystem>().CanInteract(target, target), Is.True);
            var hood = e.SpawnEntity("DLLustWishSensoryHood", coords);
            Hold(hands, target, hood);
            Request(gear, sessions[1], target, "attach");
            var effects = e.GetComponent<LustWishWornEffectsComponent>(target);
            Assert.That(effects.Blind && effects.Deaf, Is.True);
            Request(gear, sessions[1], target, "release-all");
            Assert.That(effects.Blind || effects.Deaf, Is.False);
            var glasses = e.SpawnEntity("DLLustWishHypnoGlasses", coords);
            Hold(hands, target, glasses);
            Request(gear, sessions[1], target, "attach");
            Request(gear, sessions[1], target, "toggle", "eyes", "1");
            Assert.That(effects.Focused, Is.True);
            e.DeleteEntity(glasses);
            Assert.That(effects.Focused, Is.False, "Deleting an accessory clears its effects");
            var shock = e.SpawnEntity("DLLustWishShockCollar", coords);
            Hold(hands, user, shock);
            Request(gear, sessions[0], target, "attach");
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(shock).Wearer, Is.EqualTo(target));
            var remote = e.SpawnEntity("DLLustWishRemote", coords);
            Hold(hands, user, remote);
            Request(gear, sessions[0], target, "pair", "neck");
            Assert.That(e.System<Content.Server.PowerCell.PowerCellSystem>().TryGetBatteryFromSlot(shock, out var cell, out var battery), Is.True);
            var before = battery!.CurrentCharge;
            Request(gear, sessions[0], target, "pulse", "neck");
            Assert.That(battery.CurrentCharge, Is.EqualTo(before - 10));
            Request(gear, sessions[0], target, "pulse", "neck");
            Assert.That(battery.CurrentCharge, Is.EqualTo(before - 10), "Repeated pulses respect cooldown");
            e.System<SharedTransformSystem>().SetCoordinates(user, new EntityCoordinates(map.Grid, new Vector2(40.5f, 0.5f)));
            e.EventBus.RaiseLocalEvent(remote, new UseInHandEvent(user));
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(shock).Active, Is.False, "Remote range must be checked before effects");
            escapeTarget = target; escapeItem = shock;
            Request(gear, sessions[1], target, "escape", "neck");
        });
        await pair.RunTicksSync(650);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.GetComponent<LustWishAccessoryComponent>(escapeItem).Wearer, Is.Null, "Timed escape works without a key");
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], null);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], null);
        });
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.ReallyBeIdle();
        await pair.Server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemoteUsesBatteryAndPacksAndFurnitureHaveFiniteOutputs()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(1);
        EntityUid user = default, device = default, kit = default, wipe = default, cell = default;
        float charge = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var coords = new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f));
            user = e.SpawnEntity("MobHuman", coords);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], user);
            var hands = e.System<SharedHandsSystem>();
            var gear = e.System<LustWishEquipmentSystem>();
            e.System<InteractionPanelSystem>().OpenPanel(user, user);
            device = e.SpawnEntity("DLLustWishRemoteMassager", coords);
            Hold(hands, user, device);
            Request(gear, sessions[0], user, "attach");
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(device).Wearer, Is.EqualTo(user));
            var remote = e.SpawnEntity("DLLustWishRemote", coords);
            Hold(hands, user, remote);
            Request(gear, sessions[0], user, "pair", "torso");
            Assert.That(e.GetComponent<LustWishRemoteComponent>(remote).Device, Is.EqualTo(device));
            e.EventBus.RaiseLocalEvent(remote, new UseInHandEvent(user));
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(device).Active, Is.True);
            Assert.That(e.System<Content.Server.PowerCell.PowerCellSystem>().TryGetBatteryFromSlot(device, out var battery, out var c), Is.True);
            cell = battery!.Value; charge = c!.CurrentCharge;
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<BatteryComponent>(cell).CurrentCharge, Is.LessThan(charge));
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Current, Is.GreaterThan(0));
            var containers = e.System<SharedContainerSystem>();
            containers.TryGetContainer(device, "cell_slot", out var container);
            Assert.That(containers.Remove(cell, container!), Is.True);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<LustWishAccessoryComponent>(device).Active, Is.False);
            var hands = e.System<SharedHandsSystem>();
            var coords = e.GetComponent<TransformComponent>(user).Coordinates;
            var pack = e.SpawnEntity("DLLustWishWipePack", coords);
            Hold(hands, user, pack);
            for (var i = 0; i < 5; i++) e.EventBus.RaiseLocalEvent(pack, new UseInHandEvent(user));
            Assert.That(e.GetComponent<LustWishPackComponent>(pack).Count, Is.Zero);
            var wipes = e.EntityQuery<LustWishWipeComponent>().ToArray();
            Assert.That(wipes, Has.Length.EqualTo(4));
            wipe = wipes[0].Owner;
            var solutions = e.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(wipe, "absorbed", out var solution, out _), Is.True);
            solutions.TryAddReagent(solution!.Value, "Water", 10, out _);
            Assert.That(e.GetComponent<LustWishWipeComponent>(wipe).Used, Is.True);
            kit = e.SpawnEntity("DLLustWishXStandKit", coords);
            Hold(hands, user, kit);
            e.EventBus.RaiseLocalEvent(kit, new UseInHandEvent(user));
        });
        await pair.RunTicksSync(210);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.EntityExists(kit), Is.False);
            Assert.That(e.HasComponent<Content.Shared.Fluids.AbsorbentComponent>(wipe), Is.False);
            Assert.That(e.EntityQuery<Content.Shared.Buckle.Components.StrapComponent>().Count(s => e.GetComponent<MetaDataComponent>(s.Owner).EntityPrototype?.ID == "DLLustWishXStand"), Is.EqualTo(1));
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], null);
        });
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.ReallyBeIdle();
        await pair.Server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task AttachedSpritesAndClothingTintReachTheClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid body = default;
        NetEntity bodyNet = default, collarNet = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var coords = new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f));
            body = e.SpawnEntity("MobHuman", coords);
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            var collar = e.SpawnEntity("DLLustWishCollar", coords);
            Hold(e.System<SharedHandsSystem>(), body, collar);
            e.System<InteractionPanelSystem>().OpenPanel(body, body);
            Request(e.System<LustWishEquipmentSystem>(), session, body, "attach");
            Request(e.System<LustWishEquipmentSystem>(), session, body, "color", "neck", "#AC70D4");
            var costume = e.SpawnEntity("DLLustWishColorMaid", coords);
            var tint = e.GetComponent<LustWishPaletteComponent>(costume);
            tint.Color = Color.FromHex("#AC70D4"); e.Dirty(costume, tint);
            Assert.That(e.System<Content.Shared.Inventory.InventorySystem>().TryEquip(body, costume, "jumpsuit", silent: true, force: true), Is.True);
            bodyNet = e.GetNetEntity(body); collarNet = e.GetNetEntity(collar);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var bodyClient = e.GetEntity(bodyNet);
            var collarClient = e.GetEntity(collarNet);
            Assert.That(e.GetComponent<LustWishWornEffectsComponent>(bodyClient).Items["neck"], Is.EqualTo(collarNet));
            Assert.That(e.HasComponent<LustWishAccessoryComponent>(collarClient), Is.True);
            e.System<Content.Client._DeepLagoon.LustWish.LustWishVisualSystem>().FrameUpdate(0);
            var sprite = e.GetComponent<Robust.Client.GameObjects.SpriteComponent>(bodyClient);
            Assert.That(sprite.LayerMapTryGet("lustwish/neck", out var index), Is.True);
            Assert.That(sprite.AllLayers.ElementAt(index).Color, Is.EqualTo(Color.FromHex("#AC70D4")));
            Assert.That(sprite.AllLayers.Any(l => l.RsiState.ToString() == "equipped-INNERCLOTHING" && l.Color == Color.FromHex("#AC70D4")), Is.True,
                "The native uniform layer must use the imported equipped state and current tint");
        });
        await pair.Server.WaitPost(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.ReallyBeIdle();
        await pair.CleanReturnAsync();
    }

}
