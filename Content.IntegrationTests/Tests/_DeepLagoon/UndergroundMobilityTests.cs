using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server._NF.GameRule;
using Content.Server._NF.Shuttles.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class UndergroundMobilityTests
{
    [Test]
    public async Task AuthoredUndergroundCanMoveWithAnchorOffAndOnlyGodfatherCanUseItsConsoles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var poi = prototypes.Index<PointOfInterestPrototype>("Underground");
            Assert.That(poi.AddComponents.ContainsKey("ForceAnchor"), Is.False,
                "The POI parent must not reintroduce a permanent anchor after loading the mobile map.");
            var map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(mapId,
                new ResPath("/Maps/_Mono/POI/underground.yml"), out var loaded), Is.True);
            var grid = loaded!.Value.Owner;
            Assert.That(entities.HasComponent<ForceAnchorComponent>(grid), Is.False);
            Assert.That(entities.HasComponent<PreventGridAnchorChangesComponent>(grid), Is.False);
            var consoleCount = 0;
            var privateConsoleFound = false;
            var access = entities.System<AccessReaderSystem>();
            var consoles = entities.EntityQueryEnumerator<ShuttleConsoleComponent, AccessReaderComponent, TransformComponent>();
            while (consoles.MoveNext(out _, out _, out var reader, out var transform))
            {
                if (transform.GridUid != grid) continue;
                consoleCount++;
                Assert.That(access.AreAccessTagsAllowed(new List<ProtoId<AccessLevelPrototype>> { "Godfather" }, reader), Is.True);
                Assert.That(access.AreAccessTagsAllowed(new List<ProtoId<AccessLevelPrototype>> { "Underground" }, reader), Is.False);
                if (transform.LocalPosition != new Vector2(-9.5f, 3.5f)) continue;
                privateConsoleFound = true;
                Assert.That(transform.LocalRotation.Theta, Is.EqualTo(Math.PI).Within(.0001),
                    "The private console must face north, away from its southern wall.");
            }
            Assert.That(consoleCount, Is.EqualTo(2));
            Assert.That(privateConsoleFound, Is.True);
            var directions = new HashSet<int>();
            var linear = 0;
            var angular = 0;
            var thrusters = entities.EntityQueryEnumerator<ThrusterComponent, TransformComponent>();
            while (thrusters.MoveNext(out var uid, out var thruster, out var transform))
            {
                if (transform.GridUid != grid) continue;
                Assert.That(entities.HasComponent<ApcPowerReceiverComponent>(uid), Is.True);
                if (thruster.Type == ThrusterType.Angular) angular++;
                else
                {
                    linear++;
                    directions.Add(((int)Math.Round(transform.LocalRotation.Theta / (Math.PI / 2)) + 4) % 4);
                }
            }
            Assert.That(linear, Is.EqualTo(13), "The author's complete engine layout must be preserved.");
            Assert.That(directions, Has.Count.EqualTo(4));
            Assert.That(angular, Is.GreaterThan(0));
            EntityUid anchor = default;
            var anchors = entities.EntityQueryEnumerator<StationAnchorComponent, TransformComponent>();
            while (anchors.MoveNext(out var uid, out _, out var transform))
                if (transform.GridUid == grid) anchor = uid;
            Assert.That(anchor, Is.Not.EqualTo(default(EntityUid)));
            var on = new ChargedMachineActivatedEvent();
            entities.EventBus.RaiseLocalEvent(anchor, ref on);
            Assert.That(entities.GetComponent<PhysicsComponent>(grid).BodyType, Is.EqualTo(BodyType.Static));
            var off = new ChargedMachineDeactivatedEvent();
            entities.EventBus.RaiseLocalEvent(anchor, ref off);
            Assert.That(entities.GetComponent<ShuttleComponent>(grid).Enabled, Is.True);
            Assert.That(entities.GetComponent<PhysicsComponent>(grid).BodyType, Is.EqualTo(BodyType.Dynamic));
            Assert.That(entities.GetComponent<PhysicsComponent>(grid).FixedRotation, Is.False);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
