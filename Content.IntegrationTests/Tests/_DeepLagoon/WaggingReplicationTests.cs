using System.Linq;
using System.Numerics;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using ServerAppearance = Content.Server.Humanoid.HumanoidAppearanceSystem;
using ServerWagging = Content.Server.Wagging.WaggingSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class WaggingReplicationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReplicatedWaggingDoesNotLeaveOldTailLayers(bool reenterPvs)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        NetEntity netEntity = default;
        EntityUid observer = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            pair.Server.CfgMan.SetCVar(Robust.Shared.CVars.NetPVS, true);
            var uid = entities.SpawnEntity("MobReptilian", map.GridCoords);
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
            humanoid.MarkingSet.Clear();
            entities.System<ServerAppearance>().AddMarking(uid, "VulpTail", Color.Red, forced: true, humanoid: humanoid);
            netEntity = entities.GetNetEntity(uid);
            observer = entities.SpawnEntity("MobObserver", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), observer);
        });
        await pair.RunTicksSync(15);

        foreach (var id in new[] { "LizardTailSmooth", "VulpTail", "VulpTailFox", "VoxTail", "SnakeLargeTail" })
        {
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var uid = entities.GetEntity(netEntity);
                entities.System<ServerAppearance>().SetMarkingId(uid, MarkingCategories.Tail, 0, id);
            });
            await pair.RunTicksSync(10);
            for (var iteration = 0; iteration < 4; iteration++)
            {
                await pair.Server.WaitAssertion(() =>
                {
                    var entities = pair.Server.EntMan;
                    Assert.That(entities.System<ServerWagging>().TryToggleWagging(entities.GetEntity(netEntity)), Is.True);
                    Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(entities.GetEntity(netEntity))
                        .MarkingSet.Markings[MarkingCategories.Tail], Has.Count.EqualTo(1));
                });
                await pair.RunTicksSync(5);
                if (reenterPvs)
                {
                    await pair.Server.WaitPost(() => pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(observer,
                        new EntityCoordinates(map.GridCoords.EntityId, new Vector2(200, 200))));
                    await pair.RunTicksSync(10);
                    await pair.Server.WaitPost(() => pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(observer, map.GridCoords));
                    await pair.RunTicksSync(10);
                }
                await pair.Client.WaitAssertion(() =>
                {
                    var entities = pair.Client.EntMan;
                    var uid = entities.GetEntity(netEntity);
                    var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                    var sprite = entities.GetComponent<SpriteComponent>(uid);
                    // Reapply the already-visible tail through the body/clothing visibility path.
                    // This must remain idempotent even after part containers re-enter PVS.
                    entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>()
                        .SetLayerVisibility((uid, humanoid), HumanoidVisualLayers.Tail, true);
                    var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
                    var resources = pair.Client.ResolveDependency<IResourceCache>();
                    var expectedId = iteration % 2 == 0 ? id + "Animated" : id;
                    var tailStates = prototypes.EnumeratePrototypes<MarkingPrototype>()
                        .Where(p => p.BodyPart == HumanoidVisualLayers.Tail)
                        .SelectMany(p => p.Sprites.OfType<SpriteSpecifier.Rsi>())
                        .Select(s => resources.GetResource<RSIResource>(new ResPath("/Textures") / s.RsiPath).RSI[s.RsiState]).ToHashSet();
                    var expected = prototypes.Index<MarkingPrototype>(expectedId).Sprites.OfType<SpriteSpecifier.Rsi>()
                        .Select(s => resources.GetResource<RSIResource>(new ResPath("/Textures") / s.RsiPath).RSI[s.RsiState]).ToArray();
                    var actual = sprite.AllLayers.OfType<SpriteComponent.Layer>()
                        .Where(l => l.ActualState != null && tailStates.Contains(l.ActualState))
                        .Select(l => l.ActualState).ToArray();
                    Assert.That(actual, Is.EquivalentTo(expected), $"{id}, toggle {iteration}: obsolete tail layers must be removed. " +
                        $"Actual states: {string.Join(", ", actual.Select(s => s!.StateId))}");
                });
            }
        }
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(entities.GetEntity(netEntity));
            Assert.That(humanoid.MarkingSet.Markings[MarkingCategories.Tail].Select(m => m.MarkingId),
                Is.EqualTo(new[] { "SnakeLargeTail" }), "Client appearance must match the server after every PVS/toggle cycle");
        });
        await pair.CleanReturnAsync();
    }
}
