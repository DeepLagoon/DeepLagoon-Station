using System.Linq;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Mind;
using Content.Shared.CCVar;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Configuration;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunColossusTests
{
    [TestCase("BitrunMobColossus")]
    [TestCase("BitrunMobBloodDrunkMiner")]
    [TestCase("BitrunMobWendigo")]
    [TestCase("BitrunMobAshDrake")]
    [TestCase("BitrunMobBubblegum")]
    [TestCase("BitrunMobHierophant")]
    public async Task AutonomousBossesAcquireTargetsAndStartSourceAttacks(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var config = pair.Server.ResolveDependency<IConfigurationManager>();
        var previousEnabled = config.GetCVar(CCVars.NPCEnabled);
        var previousPause = config.GetCVar(CCVars.NPCPauseWhenNoPlayersInRange);
        EntityUid boss = default;
        try
        {
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                var maps = em.System<SharedMapSystem>();
                for (var x = -7; x <= 10; x++)
                for (var y = -7; y <= 7; y++)
                    maps.SetTile(map.Grid, new Robust.Shared.Maths.Vector2i(x, y), map.Tile.Tile);
                config.SetCVar(CCVars.NPCEnabled, true);
                config.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false);
                em.System<NPCSystem>().Enabled = true;
                var target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new System.Numerics.Vector2(5.5f, 0.5f)));
                em.AddComponent<GodmodeComponent>(target);
                var minds = em.System<MindSystem>();
                minds.TransferTo(minds.CreateMind(null), target);
                boss = em.SpawnEntity(prototype, map.GridCoords.Offset(new System.Numerics.Vector2(0.5f, 0.5f)));
            });
            await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 8);
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                var started = prototype switch
                {
                    "BitrunMobColossus" => em.GetComponent<BitrunColossusComponent>(boss).NextAttack,
                    "BitrunMobBloodDrunkMiner" => em.GetComponent<BitrunMinerComponent>(boss).NextAttack,
                    "BitrunMobWendigo" => em.GetComponent<BitrunWendigoComponent>(boss).NextAttack,
                    "BitrunMobAshDrake" => em.GetComponent<BitrunDrakeComponent>(boss).NextAttack,
                    "BitrunMobBubblegum" => em.GetComponent<BitrunBubblegumComponent>(boss).NextAttack,
                    _ => em.GetComponent<BitrunHierophantComponent>(boss).NextAttack,
                };
                Assert.That(started, Is.GreaterThan(TimeSpan.Zero), "Native AI must acquire the target and invoke the source attack without a test calling StartAttack.");
            });
        }
        finally
        {
            await pair.Server.WaitPost(() =>
            {
                config.SetCVar(CCVars.NPCEnabled, previousEnabled);
                config.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, previousPause);
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WendigoSlamHurtsThenHalfHealthUnlocksWavesAndCurvingProjectiles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid boss = default;
        EntityUid target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            boss = em.SpawnEntity("BitrunMobWendigo", map.GridCoords);
            target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new System.Numerics.Vector2(2, 0)));
            em.GetComponent<HTNComponent>(boss).Enabled = false;
            var comp = em.GetComponent<BitrunWendigoComponent>(boss);
            var system = em.System<BitrunWendigoSystem>();
            Assert.That(system.StartAttack((boss, comp), target, BitrunWendigoAttack.Wave), Is.False);
            Assert.That(system.StartAttack((boss, comp), target, BitrunWendigoAttack.Slam), Is.True);
            Assert.That(comp.Rooted, Is.True);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.GreaterThanOrEqualTo(20));
            var comp = em.GetComponent<BitrunWendigoComponent>(boss);
            Assert.That(comp.Rooted, Is.False);
            var damage = new DamageSpecifier();
            damage.DamageDict["Blunt"] = 625;
            em.System<DamageableSystem>().TryChangeDamage(boss, damage, true, ignoreGlobalModifiers: true);
            Assert.That(comp.Enraged, Is.True);
            comp.NextAttack = TimeSpan.Zero;
            Assert.That(em.System<BitrunWendigoSystem>().StartAttack((boss, comp), target, BitrunWendigoAttack.Wave), Is.True);
            Assert.That(comp.Shots.Count, Is.EqualTo(72));
            Assert.That(comp.Shots.All(shot => shot.Speed == 1.25f && Math.Abs(shot.Curve) == 9.6f), Is.True);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<BitrunWendigoComponent>(boss).FiredShots, Is.GreaterThanOrEqualTo(6));
            var curves = em.EntityQueryEnumerator<BitrunCurvingProjectileComponent>();
            Assert.That(curves.MoveNext(out _, out _), Is.True, "Waves must launch real projectiles with trajectory curvature.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MinerDashesOntoReachableGroundAndFiresItsSourceAccelerator()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid miner = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            for (var x = -4; x <= 8; x++)
            for (var y = -4; y <= 4; y++)
                maps.SetTile(map.Grid, new Robust.Shared.Maths.Vector2i(x, y), map.Tile.Tile);
            miner = em.SpawnEntity("BitrunMobBloodDrunkMiner", map.GridCoords.Offset(new System.Numerics.Vector2(0.5f, 0.5f)));
            var target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new System.Numerics.Vector2(5.5f, 0.5f)));
            em.GetComponent<HTNComponent>(miner).Enabled = false;
            var comp = em.GetComponent<BitrunMinerComponent>(miner);
            Assert.That(em.System<BitrunMinerSystem>().StartAttack((miner, comp), target), Is.True);
            Assert.That(em.GetComponent<TransformComponent>(miner).Coordinates.X, Is.GreaterThan(3));
            Assert.That(comp.SawOpen, Is.True);
            Assert.That(em.GetComponent<MobThresholdsComponent>(miner).Thresholds.Keys.Max().Float(), Is.EqualTo(600));
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<BitrunMinerComponent>(miner).FiredShots, Is.EqualTo(1));
            var damage = new Content.Shared.Weapons.Melee.Events.GetMeleeDamageEvent(miner, new DamageSpecifier(), new(), miner);
            em.EventBus.RaiseLocalEvent(miner, ref damage);
            Assert.That(damage.Damage.DamageDict["Slash"].Float(), Is.EqualTo(10));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SourceSpiralFiresAndRageUnlocksDoubleSpiralAndSingleFinale()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid boss = default;
        EntityUid target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            boss = em.SpawnEntity("BitrunMobColossus", map.GridCoords);
            target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new System.Numerics.Vector2(20, 0)));
            em.GetComponent<HTNComponent>(boss).Enabled = false;
            var comp = em.GetComponent<BitrunColossusComponent>(boss);
            Assert.That(em.GetComponent<MobThresholdsComponent>(boss).Thresholds.Keys.Max().Float(), Is.EqualTo(1250));
            Assert.That(em.System<BitrunColossusSystem>().StartAttack((boss, comp), target, BitrunColossusAttack.Spiral), Is.True);
            Assert.That(comp.Shots.Count, Is.EqualTo(80));
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var comp = em.GetComponent<BitrunColossusComponent>(boss);
            Assert.That(comp.FiredShots, Is.GreaterThan(0), "The scheduled attack must launch actual projectiles.");
            Assert.That(em.GetComponent<DamageableComponent>(boss).TotalDamage.Float(), Is.Zero, "Megafauna must tolerate the test map's vacuum.");
            comp.Shots.Clear();
            comp.NextAttack = TimeSpan.Zero;
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", 850);
            em.System<DamageableSystem>().TryChangeDamage(boss, damage, true, ignoreGlobalModifiers: true);
            Assert.That(comp.Enraged, Is.True);
            Assert.That(em.System<BitrunColossusSystem>().StartAttack((boss, comp), target, BitrunColossusAttack.Spiral), Is.True);
            Assert.That(comp.Shots.Count, Is.EqualTo(160));
            comp.Shots.Clear();
            comp.NextAttack = TimeSpan.Zero;
            damage.DamageDict["Blunt"] = 300;
            em.System<DamageableSystem>().TryChangeDamage(boss, damage, true, ignoreGlobalModifiers: true);
            Assert.That(em.GetComponent<DamageableComponent>(boss).TotalDamage.Float(), Is.EqualTo(1150), "Finale starts at 100 HP.");
            Assert.That(em.GetComponent<MobStateComponent>(boss).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(comp.FinaleUsed, Is.False);
            Assert.That(em.System<BitrunColossusSystem>().StartAttack((boss, comp), target, BitrunColossusAttack.Finale), Is.True);
            comp.Shots.Clear();
            comp.NextAttack = TimeSpan.Zero;
            Assert.That(em.System<BitrunColossusSystem>().StartAttack((boss, comp), target, BitrunColossusAttack.Finale), Is.False);
            damage.DamageDict["Blunt"] = 200;
            em.System<DamageableSystem>().TryChangeDamage(boss, damage, true, ignoreGlobalModifiers: true);
        });
        await pair.Server.WaitRunTicks(2);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.Deleted(boss) || pair.Server.EntMan.GetComponent<BitrunColossusComponent>(boss).Shots.Count == 0, Is.True));
        await pair.CleanReturnAsync();
    }
}
