using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Linq;
using Content.Client._Mono.Radar;
using Content.Shared._Mono.Radar;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class RadarContactPlaybackTests
{
    [TestCase("ComputerRadar")]
    [TestCase("ComputerGunneryConsole")]
    public async System.Threading.Tasks.Task AnOpenConsoleReceivesRealProjectileContactsOverTheNetwork(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server.EntMan;
        var client = pair.Client.EntMan;
        EntityUid console = default, secondConsole = default, projectile = default;
        await pair.Server.WaitAssertion(() =>
        {
            var actor = server.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
            console = server.SpawnEntity(prototype, map.GridCoords);
            server.RemoveComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(console);
            Enum key = prototype == "ComputerRadar" ? Content.Shared.Shuttles.BUIStates.RadarConsoleUiKey.Key : Content.Shared._Mono.FireControl.FireControlConsoleUiKey.Key;
            server.System<SharedUserInterfaceSystem>().OpenUi(console, key, actor);
            secondConsole = server.SpawnEntity("ComputerRadar", map.GridCoords);
            server.RemoveComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(secondConsole);
            server.System<SharedUserInterfaceSystem>().OpenUi(secondConsole, Content.Shared.Shuttles.BUIStates.RadarConsoleUiKey.Key, actor);
            projectile = server.SpawnEntity("20mmBullet", new EntityCoordinates(map.MapUid, new Vector2(100, 100)));
            server.RemoveComponent<Robust.Shared.Spawners.TimedDespawnComponent>(projectile);
        });
        var netConsole = server.GetNetEntity(console);
        var netSecond = server.GetNetEntity(secondConsole);
        var netProjectile = server.GetNetEntity(projectile);
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            client.System<RadarBlipsSystem>().RequestBlips(client.GetEntity(netConsole));
            client.System<RadarBlipsSystem>().RequestBlips(client.GetEntity(netSecond));
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var contacts = client.System<RadarBlipsSystem>().GetCurrentBlips(client.GetEntity(netConsole));
            Assert.That(contacts.Any(b => b.NetUid == netProjectile), Is.True,
                "An authorized real console must display the actual projectile from the server response.");
            Assert.That(client.System<RadarBlipsSystem>().GetCurrentBlips(client.GetEntity(netSecond)).Any(b => b.NetUid == netProjectile), Is.True,
                "Simultaneous requests from two open consoles must both receive a response.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async System.Threading.Tasks.Task DelayedOutOfOrderAndForeignRadarReportsCannotReplayOldProjectiles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var client = pair.Client; var e = client.EntMan; var system = e.System<RadarBlipsSystem>();
        EntityUid a = default, b = default, map = default, secondMap = default;
        TimeSpan sample = default;
        Robust.Shared.GameObjects.NetEntity shell = default;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        void Track(EntityUid radar) => typeof(RadarBlipsSystem).GetMethod("Report", hidden)!.Invoke(system, [radar, true]);
        GiveBlipsEvent Packet(EntityUid radar, EntityUid reportMap, TimeSpan time, float x) => new(
            [new()], [new(shell, new(e.GetNetEntity(reportMap), new Vector2(x, 0)), new(50, 0), default, 0, null)],
            [new(shell, 100, default)], [], e.GetNetEntity(radar), e.GetNetEntity(reportMap), time);
        await client.WaitAssertion(() =>
        {
            var maps = e.System<SharedMapSystem>(); map = maps.CreateMap(out _); secondMap = maps.CreateMap(out _);
            a = e.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero)); b = e.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
            shell = e.GetNetEntity(e.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero)));
            Track(a); Track(b); sample = client.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(.2);
            Assert.That(system.ApplyReport(Packet(a, map, sample, 10)), Is.True);
            Assert.That(system.ApplyReport(Packet(b, map, sample, 1000)), Is.True);
            Assert.That(system.GetCurrentBlips(a)[0].Position.X, Is.EqualTo(20).Within(.1));
            Assert.That(system.GetCurrentBlips(b)[0].Position.X, Is.EqualTo(1010).Within(.1), "Two open consoles own independent snapshots.");
            Assert.That(system.GetMissileLines(a)[0].PositionStart.NetEntity, Is.EqualTo(e.GetNetEntity(map)), "Vectors use the map frame, never the projectile as their coordinate parent.");
            Assert.That(system.ApplyReport(Packet(a, map, sample - TimeSpan.FromSeconds(.1), -5000)), Is.False);
            Assert.That(system.ApplyReport(Packet(a, secondMap, sample + TimeSpan.FromSeconds(.1), -5000)), Is.False);
            Assert.That(system.ApplyReport(Packet(a, map, sample - TimeSpan.FromSeconds(5), -5000)), Is.False);
            var removal = new BlipRemovalEvent(shell, sample + TimeSpan.FromSeconds(.1));
            typeof(RadarBlipsSystem).GetMethod("RemoveBlip", hidden)!.Invoke(system, [removal]);
            Assert.That(system.GetCurrentBlips(a), Is.Empty); Assert.That(system.GetMissileLines(a), Is.Empty);
            Assert.That(system.ApplyReport(Packet(a, map, sample + TimeSpan.FromSeconds(.05), 10)), Is.True);
            Assert.That(system.GetCurrentBlips(a), Is.Empty, "A delayed pre-removal report cannot resurrect an expired projectile.");
            Assert.That(system.ApplyReport(Packet(a, map, sample + TimeSpan.FromSeconds(.15), 50)), Is.True);
            Assert.That(system.GetCurrentBlips(a), Has.Count.EqualTo(1));
            e.System<SharedTransformSystem>().SetCoordinates(b, new EntityCoordinates(secondMap, Vector2.Zero));
            Assert.That(system.GetCurrentBlips(b), Is.Empty, "Jumping to another map clears the previous sector's contacts.");
        });
        await client.WaitRunTicks(240);
        await client.WaitAssertion(() =>
        {
            Assert.That(system.GetCurrentBlips(a), Is.Empty); Assert.That(system.GetMissileLines(a), Is.Empty);
            Assert.That(system.ApplyReport(Packet(a, map, sample, 10)), Is.False, "Receiving an old packet does not reset its age.");
        });
        await pair.CleanReturnAsync();
    }
}
