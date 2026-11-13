using Content.Server.Gravity;
using Content.Server.Movement.Systems;
using Content.Shared.Atmos.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Movement.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class JetpackStandbyTests
{
    [Test]
    public async Task ArmedPackResumesInSpaceAndLosesSelectionWhenDropped()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        EntityUid pack = default;
        float initialFuel = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<GravitySystem>().EnableGravity(map.Grid);
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            pack = entities.SpawnEntity("JetpackBlueFilled", map.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, pack, false, false), Is.True);
            var jetpacks = entities.System<JetpackSystem>();
            var component = entities.GetComponent<JetpackComponent>(pack);
            jetpacks.SetEnabled(pack, component, true, user);
            initialFuel = entities.GetComponent<GasTankComponent>(pack).Air.TotalMoles;
            Assert.That(component.JetpackUser, Is.EqualTo(user));
            Assert.That(jetpacks.IsUserFlying(user), Is.False);
        });

        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<GasTankComponent>(pack).Air.TotalMoles, Is.EqualTo(initialFuel));
            entities.System<SharedTransformSystem>().SetCoordinates(user, new EntityCoordinates(map.MapUid, 50, 50));
            Assert.That(entities.System<JetpackSystem>().IsUserFlying(user), Is.True);
            Assert.That(entities.HasComponent<ActiveJetpackComponent>(pack), Is.True);

            entities.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords);
            Assert.That(entities.GetComponent<JetpackComponent>(pack).JetpackUser, Is.EqualTo(user));
            Assert.That(entities.System<JetpackSystem>().IsUserFlying(user), Is.False);
            Assert.That(entities.HasComponent<ActiveJetpackComponent>(pack), Is.False);

            entities.System<SharedTransformSystem>().SetCoordinates(user, new EntityCoordinates(map.MapUid, 50, 50));
            Assert.That(entities.System<JetpackSystem>().IsUserFlying(user), Is.True);
            Assert.That(entities.System<SharedHandsSystem>().TryDrop(user, pack, checkActionBlocker: false), Is.True);
            Assert.That(entities.GetComponent<JetpackComponent>(pack).JetpackUser, Is.Null);
            Assert.That(entities.System<JetpackSystem>().IsUserFlying(user), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}
