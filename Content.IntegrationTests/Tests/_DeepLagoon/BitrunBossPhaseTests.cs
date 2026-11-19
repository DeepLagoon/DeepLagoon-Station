using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.NPC.HTN;
using Content.Server.Temperature.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Projectiles;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunBossPhaseTests
{
    [Test]
    public async Task DrakeBreathHurtsSwoopIsInvulnerableAndHalfHealthBuildsDodgeArena()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid boss = default, target = default;
        await pair.Server.WaitAssertion(() =>
        {
            Floor(em, map.Grid, map.Tile.Tile);
            target = Human(em, map.GridCoords.Offset(new Vector2(3.5f, 0.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), target);
            boss = em.SpawnEntity("BitrunMobAshDrake", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            em.GetComponent<HTNComponent>(boss).Enabled = false;
            Assert.That(em.System<BitrunDrakeSystem>().StartAttack((boss, em.GetComponent<BitrunDrakeComponent>(boss)), target, BitrunDrakeAttack.Cone), Is.True);
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.Zero);
        });
        await pair.RunSeconds(0.7f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<DamageableComponent>(target).Damage.DamageDict["Heat"].Float(), Is.EqualTo(20));
            em.AddComponent<GodmodeComponent>(target);
            Hurt(em, boss, 626);
            var comp = em.GetComponent<BitrunDrakeComponent>(boss);
            Assert.That(comp.Enraged, Is.True);
            comp.NextAttack = TimeSpan.Zero;
            Assert.That(em.System<BitrunDrakeSystem>().StartAttack((boss, comp), target, BitrunDrakeAttack.Swoop), Is.True);
        });
        await pair.RunSeconds(0.5f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<BitrunDrakeComponent>(boss).Invulnerable, Is.True);
            Hurt(em, boss, 10);
            Assert.That(em.GetComponent<DamageableComponent>(boss).TotalDamage.Float(), Is.EqualTo(626));
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            var comp = em.GetComponent<BitrunDrakeComponent>(boss);
            Assert.That(comp.Stage, Is.EqualTo(BitrunDrakeStage.Arena));
            Assert.That(comp.ArenaRound, Is.EqualTo(1));
            Assert.That(comp.ArenaWalls.Count, Is.EqualTo(24));
            Assert.That(comp.ArenaPlayers.Contains(target), Is.True);
            Assert.That(em.EntityQuery<BitrunBossHazardComponent>().Count(hazard => hazard.LavaDuration == 1), Is.GreaterThan(0));
        });
        await pair.RunSeconds(9);
        await pair.Server.WaitAssertion(() =>
        {
            var comp = em.GetComponent<BitrunDrakeComponent>(boss);
            Assert.That(comp.ArenaRound, Is.EqualTo(3));
            Assert.That(comp.Stage, Is.EqualTo(BitrunDrakeStage.Ground));
            Assert.That(comp.Invulnerable, Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HierophantWarnsBeforeDamageBlinksAndRageRunsRepeatedAttacks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid boss = default, target = default;
        await pair.Server.WaitAssertion(() =>
        {
            Floor(em, map.Grid, map.Tile.Tile);
            boss = em.SpawnEntity("BitrunMobHierophant", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            target = Human(em, map.GridCoords.Offset(new Vector2(5.5f, 0.5f)));
            em.GetComponent<HTNComponent>(boss).Enabled = false;
            Assert.That(em.System<BitrunHierophantSystem>().StartAttack((boss, em.GetComponent<BitrunHierophantComponent>(boss)), target, BitrunHierophantAttack.Cross), Is.True);
        });
        await pair.RunSeconds(0.5f);
        await pair.Server.WaitAssertion(() => Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.Zero));
        await pair.RunSeconds(0.5f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<DamageableComponent>(target).Damage.DamageDict["Heat"].Float(), Is.EqualTo(10));
            var comp = em.GetComponent<BitrunHierophantComponent>(boss);
            comp.NextAttack = TimeSpan.Zero;
            Assert.That(em.System<BitrunHierophantSystem>().StartAttack((boss, comp), target, BitrunHierophantAttack.Blink), Is.True);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<TransformComponent>(boss).Coordinates.Position, Is.EqualTo(new Vector2(5.5f, 0.5f)));
            Hurt(em, boss, 840);
            var comp = em.GetComponent<BitrunHierophantComponent>(boss);
            Assert.That(comp.Rage, Is.EqualTo(20));
            Assert.That(comp.BurstRange, Is.EqualTo(5));
            Assert.That(comp.BeamRange, Is.EqualTo(7));
            comp.NextAttack = TimeSpan.Zero;
            Assert.That(em.System<BitrunHierophantSystem>().StartAttack((boss, comp), target, BitrunHierophantAttack.CrossSpam), Is.True);
            Assert.That(comp.Events.Count(ev => ev.Kind == "cross-target"), Is.EqualTo(3));
        });
        await pair.RunSeconds(2.5f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<DamageableComponent>(target).Damage.DamageDict["Heat"].Float(), Is.GreaterThanOrEqualTo(50));
            em.AddComponent<GodmodeComponent>(target);
            var comp = em.GetComponent<BitrunHierophantComponent>(boss);
            comp.BusyUntil = TimeSpan.Zero; comp.NextAttack = TimeSpan.Zero;
            Assert.That(em.System<BitrunHierophantSystem>().StartAttack((boss, comp), target, BitrunHierophantAttack.ChaserSwarm), Is.True);
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() => Assert.That(em.EntityQuery<BitrunHierophantChaserComponent>().Count(), Is.GreaterThanOrEqualTo(2)));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BubblegumUsesCleanableBloodDelayedHandsRealChargesAndHalfHealthClones()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid boss = default, target = default, incoming = default;
        await pair.Server.WaitAssertion(() =>
        {
            Floor(em, map.Grid, map.Tile.Tile);
            boss = em.SpawnEntity("BitrunMobBubblegum", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            target = Human(em, map.GridCoords.Offset(new Vector2(4.5f, 0.5f)));
            em.GetComponent<HTNComponent>(boss).Enabled = false;
            var system = em.System<BitrunBubblegumSystem>();
            system.BloodHand((boss, em.GetComponent<BitrunBubblegumComponent>(boss)), em.GetComponent<TransformComponent>(target).Coordinates, false, true);
        });
        await pair.RunSeconds(0.2f);
        await pair.Server.WaitAssertion(() => Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.Zero));
        await pair.RunSeconds(0.3f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<DamageableComponent>(target).Damage.DamageDict["Blunt"].Float(), Is.EqualTo(10));
            var comp = em.GetComponent<BitrunBubblegumComponent>(boss);
            Assert.That(em.System<BitrunBubblegumSystem>().StartAttack((boss, comp), target, BitrunBubblegumAttack.TripleCharge), Is.True);
        });
        await pair.RunSeconds(1.2f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<DamageableComponent>(target).Damage.DamageDict["Blunt"].Float(), Is.EqualTo(40));
            Assert.That(em.GetComponent<TransformComponent>(boss).Coordinates.Position.X, Is.GreaterThan(4));
            em.AddComponent<GodmodeComponent>(target);
        });
        await pair.RunSeconds(3);
        await pair.Server.WaitAssertion(() =>
        {
            var comp = em.GetComponent<BitrunBubblegumComponent>(boss);
            var system = em.System<BitrunBubblegumSystem>();
            Hurt(em, boss, 625);
            Assert.That(comp.Smash, Is.True);
            comp.NextAttack = TimeSpan.Zero;
            Assert.That(system.StartAttack((boss, comp), target, BitrunBubblegumAttack.HallucinationCharge), Is.True);
        });
        await pair.RunSeconds(0.3f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.EntityQuery<BitrunBubblegumChargeComponent>().Count(charge => charge.Clone), Is.GreaterThanOrEqualTo(3));
            var comp = em.GetComponent<BitrunBubblegumComponent>(boss);
            comp.EnrageUntil = pair.Server.Timing.CurTime + TimeSpan.FromSeconds(7);
            var projectile = em.SpawnEntity("BulletPistol", map.GridCoords);
            var reflect = new ProjectileReflectAttemptEvent(projectile, em.GetComponent<ProjectileComponent>(projectile), false);
            em.EventBus.RaiseLocalEvent(boss, ref reflect);
            Assert.That(reflect.Cancelled, Is.True);
            comp.Remaining = 0;
            em.RemoveComponent<BitrunBubblegumChargeComponent>(boss);
            incoming = em.SpawnEntity("BulletPistol", em.GetComponent<TransformComponent>(boss).Coordinates.Offset(-Vector2.UnitX * 3));
            em.System<Robust.Shared.Physics.Systems.SharedPhysicsSystem>().SetLinearVelocity(incoming, Vector2.UnitX * 10);
        });
        await pair.RunSeconds(5);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.Deleted(incoming), Is.True, "An actual incoming projectile is consumed by blood rage.");
            Assert.That(em.GetComponent<DamageableComponent>(boss).TotalDamage.Float(), Is.EqualTo(625));
            var comp = em.GetComponent<BitrunBubblegumComponent>(boss);
            comp.EnrageUntil = TimeSpan.Zero;
            em.System<SharedTransformSystem>().SetCoordinates(boss, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            em.System<SharedTransformSystem>().SetCoordinates(target, map.GridCoords.Offset(new Vector2(18.5f, 0.5f)));
            var system = em.System<BitrunBubblegumSystem>();
            system.LeaveBlood(map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            system.LeaveBlood(map.GridCoords.Offset(new Vector2(13.5f, 0.5f)));
            Assert.That(system.BloodWarp((boss, comp), target), Is.True);
        });
        await pair.RunSeconds(0.5f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<TransformComponent>(boss).Coordinates.Position.X, Is.EqualTo(13.5f));
            em.RemoveComponent<GodmodeComponent>(target);
            em.System<Content.Shared.Mobs.Systems.MobStateSystem>().ChangeMobState(target, Content.Shared.Mobs.MobState.Critical);
            em.System<BitrunBubblegumSystem>().BloodHand((boss, em.GetComponent<BitrunBubblegumComponent>(boss)),
                em.GetComponent<TransformComponent>(target).Coordinates, true, false);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.HasComponent<BitrunGuttedComponent>(target), Is.True);
            Assert.That(em.System<Content.Shared.Mobs.Systems.MobStateSystem>().IsDead(target), Is.True);
            Assert.That(em.GetComponent<DamageableComponent>(boss).TotalDamage.Float(), Is.EqualTo(575));
            Assert.That(em.System<BitrunMegafaunaSystem>().Devour(boss, target), Is.False, "The same corpse cannot heal the boss twice.");
        });
        await pair.CleanReturnAsync();
    }

    private static void Hurt(IEntityManager em, EntityUid uid, float amount) => em.System<DamageableSystem>().TryChangeDamage(uid,
        new DamageSpecifier { DamageDict = { ["Blunt"] = amount } }, true, ignoreGlobalModifiers: true);

    private static EntityUid Human(IEntityManager em, Robust.Shared.Map.EntityCoordinates coordinates)
    {
        var uid = em.SpawnEntity("MobHuman", coordinates);
        em.RemoveComponent<RespiratorComponent>(uid);
        em.RemoveComponent<BarotraumaComponent>(uid);
        em.RemoveComponent<TemperatureComponent>(uid);
        em.RemoveComponent<PassiveDamageComponent>(uid);
        return uid;
    }

    private static void Floor(IEntityManager em, EntityUid grid, Robust.Shared.Map.Tile tile)
    {
        var entity = new Entity<Robust.Shared.Map.Components.MapGridComponent>(grid, em.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid));
        for (var x = -16; x <= 20; x++)
        for (var y = -16; y <= 16; y++) em.System<SharedMapSystem>().SetTile(entity, new Vector2i(x, y), tile);
    }
}
