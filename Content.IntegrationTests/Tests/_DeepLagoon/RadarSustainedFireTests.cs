using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._Mono.Radar;
using Content.Shared._Mono.Radar;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class RadarSustainedFireTests
{
    [TestCase("ComputerShuttle")]
    [TestCase("ComputerGunneryConsole")]
    public async Task RepeatedL85AndAK570ShotsKeepReachingTheOpenRadar(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap(); var server = pair.Server.EntMan; var client = pair.Client.EntMan;
        EntityUid console = default; EntityUid[] guns = [];
        await pair.Server.WaitAssertion(() =>
        {
            var actor = server.SpawnEntity("MobHuman", map.GridCoords);
            server.EnsureComponent<Content.Shared.Damage.Components.GodmodeComponent>(actor);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
            console = server.SpawnEntity(prototype, map.GridCoords);
            server.RemoveComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(console);
            var key = prototype == "ComputerShuttle" ? (Enum) Content.Shared.Shuttles.Components.ShuttleConsoleUiKey.Key
                : Content.Shared._Mono.FireControl.FireControlConsoleUiKey.Key;
            server.System<SharedUserInterfaceSystem>().OpenUi(console, key, actor);
            guns = new EntityUid[2];
            for (var i = 0; i < 2; i++)
            {
                var coordinates = new EntityCoordinates(map.Grid.Owner, new Vector2(i + .5f, .5f));
                server.SpawnEntity("HardpointBallisticLight", coordinates);
                guns[i] = server.SpawnEntity(i == 0 ? "WeaponTurretL85Autocannon" : "WeaponTurretAK570", coordinates);
            }
        });
        var netConsole = server.GetNetEntity(console);
        await pair.RunTicksSync(30);
        var seenTypes = new HashSet<string>();
        for (var burst = 0; burst < 12; burst++)
        {
            var fired = new Dictionary<NetEntity, string>();
            HashSet<EntityUid> before = [];
            await pair.Server.WaitAssertion(() =>
            {
                before = server.EntityQuery<ProjectileComponent>().Select(p => p.Owner).ToHashSet();
                foreach (var gun in guns)
                {
                    var component = server.GetComponent<GunComponent>(gun);
                    // Fire control uses this timed trigger, including the manual-fire override
                    // for an idle AutoShootGun. A bare AttemptShoot cannot fire an unpowered AK570.
                    server.System<SharedGunSystem>().AttemptShots(gun, gun, component,
                        new EntityCoordinates(map.MapUid, new Vector2(0, 1000)), TimeSpan.FromSeconds(.3));
                }
            });
            await pair.RunTicksSync(3);
            await pair.Server.WaitAssertion(() =>
            {
                foreach (var p in server.EntityQuery<ProjectileComponent>().Where(p => !before.Contains(p.Owner)))
                    fired[server.GetNetEntity(p.Owner)] = server.GetComponent<MetaDataComponent>(p.Owner).EntityPrototype!.ID;
                Assert.That(fired.Count, Is.GreaterThanOrEqualTo(2), $"Burst {burst}: both mounted guns must actually create projectiles: {string.Join(",", fired.Values)}");
            });
            await pair.Client.WaitAssertion(() =>
            {
                var radar = client.System<RadarBlipsSystem>(); var uid = client.GetEntity(netConsole);
                // Simulation ticks run faster than wall time in the harness. Permit this next
                // real poll while retaining production throttling and authorization on the server.
                var state = typeof(RadarBlipsSystem).GetMethod("Report", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(radar, [uid, true])!;
                state.GetType().GetField("NextRequest")!.SetValue(state, TimeSpan.Zero);
                state.GetType().GetField("Awaiting")!.SetValue(state, false);
                radar.RequestBlips(uid);
            });
            await pair.RunTicksSync(30);
            await pair.Client.WaitAssertion(() =>
            {
                var current = client.System<RadarBlipsSystem>().GetCurrentBlips(client.GetEntity(netConsole));
                var visible = fired.Where(p => current.Any(b => b.NetUid == p.Key)).ToArray();
                Assert.That(visible.Select(p => p.Value).Distinct().Count(), Is.GreaterThanOrEqualTo(2), $"Burst {burst}: current shells from both L85 and AK570, including later bursts, must be present.");
                foreach (var shot in visible) seenTypes.Add(shot.Value);
            });
        }
        TestContext.Out.WriteLine("Repeated real gun projectile types: " + string.Join(", ", seenTypes));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AnchoredContactsFollowTheSameHullAfterTranslationAndRotation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan; var maps = e.System<SharedMapSystem>(); var transforms = e.System<SharedTransformSystem>();
            var map = maps.CreateMap(out _); var grid = maps.CreateGridEntity(map);
            var console = e.SpawnEntity(null, new EntityCoordinates(grid, Vector2.Zero));
            var radar = e.System<RadarBlipsSystem>();
            typeof(RadarBlipsSystem).GetMethod("Report", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(radar, [console, true]);
            var local = new EntityCoordinates(grid, new Vector2(3, 5));
            var contact = e.GetNetEntity(e.SpawnEntity(null, local));
            var sample = pair.Client.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(.2);
            Assert.That(radar.ApplyReport(new([new()], [new(contact, e.GetNetCoordinates(new EntityCoordinates(map, new Vector2(999, 999))),
                new Vector2(100, 100), default, 0, null, new(e.GetNetCoordinates(local), Angle.FromDegrees(15)))], [], [],
                e.GetNetEntity(console), e.GetNetEntity(map), sample)), Is.True);
            foreach (var degrees in new[] { 0, 90, 180 })
            {
                transforms.SetCoordinates(grid, new EntityCoordinates(map, new Vector2(200 + degrees, 300)));
                transforms.SetLocalRotation(grid, Angle.FromDegrees(degrees));
                var blip = radar.GetCurrentBlips(console).Single();
                Assert.That(blip.Position, Is.EqualTo(local), "Equipment stays in hull-local coordinates despite an old map-space sample.");
                Assert.That(blip.GridUid, Is.EqualTo(grid.Owner));
                Assert.That(blip.Rotation.Theta, Is.EqualTo(Angle.FromDegrees(degrees + 15).Theta).Within(.001));
                Assert.That(transforms.ToMapCoordinates(blip.Position), Is.EqualTo(transforms.ToMapCoordinates(local)));
            }
        });
        await pair.CleanReturnAsync();
    }
}
