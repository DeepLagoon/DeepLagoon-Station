using System.Linq;
using Content.Server._DeepLagoon.Modsuits;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Alert;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ModLinkLifecycleTests
{
    [Test]
    public async Task IncomingAlertAndCrossMapProjectionFollowTheCallLifecycle()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var callerMap = await pair.CreateTestMap();
        var receiverMap = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var inventory = entities.System<InventorySystem>();
        var alerts = entities.System<AlertsSystem>();
        EntityUid caller = default, receiver = default, callerTerminal = default, receiverTerminal = default;
        EntityUid callerHologram = default, receiverHologram = default;

        await pair.Server.WaitAssertion(() =>
        {
            caller = entities.SpawnEntity("MobHuman", callerMap.GridCoords);
            receiver = entities.SpawnEntity("MobHuman", receiverMap.GridCoords);
            callerTerminal = entities.SpawnEntity("ModLinkTerminal", callerMap.GridCoords);
            receiverTerminal = entities.SpawnEntity("ModLinkTerminal", receiverMap.GridCoords);
            Assert.That(inventory.TryEquip(caller, callerTerminal, "neck", silent: true, force: true), Is.True);
            Assert.That(inventory.TryEquip(receiver, receiverTerminal, "neck", silent: true, force: true), Is.True);
            var callerLink = entities.GetComponent<ModLinkComponent>(callerTerminal);
            var receiverLink = entities.GetComponent<ModLinkComponent>(receiverTerminal);

            void Call() => Send(callerTerminal, caller, ModOperation.Call, entities.GetNetEntity(receiverTerminal));

            receiverLink.Frequency = callerLink.Frequency + 1;
            Call();
            Assert.That(callerLink.Peer, Is.Null);
            Assert.That(alerts.IsShowingAlert(receiver, "ModLinkIncomingCall"), Is.False);

            receiverLink.Frequency = callerLink.Frequency;
            Call();
            Assert.That(receiverLink.Incoming, Is.True);
            Assert.That(alerts.IsShowingAlert(receiver, "ModLinkIncomingCall"), Is.True);

            Send(receiverTerminal, caller, ModOperation.Accept);
            Assert.That(receiverLink.Connected, Is.False);
            Send(receiverTerminal, receiver, ModOperation.Accept);
            Assert.That(callerLink.Connected && receiverLink.Connected, Is.True);
            Assert.That(alerts.IsShowingAlert(receiver, "ModLinkIncomingCall"), Is.False);
            callerHologram = callerLink.Hologram!.Value;
            receiverHologram = receiverLink.Hologram!.Value;

            Assert.That(entities.GetComponent<TransformComponent>(callerHologram).ParentUid, Is.EqualTo(receiver));
            Assert.That(entities.GetComponent<TransformComponent>(receiverHologram).ParentUid, Is.EqualTo(caller));
            Assert.That(entities.EntityQuery<HumanoidAppearanceComponent, TransformComponent>()
                .Any(body => body.Item2.ParentUid == callerHologram), Is.True);

            var hat = entities.SpawnEntity("ClothingHeadHatHardhatYellow", callerMap.GridCoords);
            Assert.That(inventory.TryEquip(caller, hat, "head", silent: true, force: true), Is.True);
        });

        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var projectedHat = entities.EntityQuery<ModLinkProjectedClothingComponent>()
                .Single(item => item.Appearance?.Prototype.Id == "ClothingHeadHatHardhatYellow");
            var avatar = entities.GetComponent<TransformComponent>(projectedHat.Owner).ParentUid;
            Assert.That(entities.GetComponent<TransformComponent>(avatar).ParentUid, Is.EqualTo(callerHologram));

            Assert.That(inventory.TryUnequip(receiver, "neck", silent: true, force: true), Is.True);
            Assert.That(entities.GetComponent<ModLinkComponent>(callerTerminal).Peer, Is.Null);
            Assert.That(entities.GetComponent<ModLinkComponent>(receiverTerminal).Peer, Is.Null);
        });

        await pair.RunTicksSync(3);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.EntityExists(callerHologram), Is.False);
            Assert.That(entities.EntityExists(receiverHologram), Is.False);
            Assert.That(entities.EntityQuery<ModLinkProjectedClothingComponent>(), Is.Empty);
        });
        await pair.CleanReturnAsync();

        void Send(EntityUid terminal, EntityUid actor, ModOperation operation, NetEntity target = default)
        {
            entities.EventBus.RaiseLocalEvent(terminal, new ModUiMessage(operation, target) { Actor = actor, UiKey = ModUiKey.Key });
        }
    }
}
