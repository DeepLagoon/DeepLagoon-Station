using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.CCVar;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Hitscan.Events;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class HolographicForceFieldTests
{
    [TestCase("HolosignForcefield", true)]
    [TestCase("HolosignForcefieldTSF", false)]
    public async Task LaserBeamsAndBoltsRespectFieldVariant(string prototype, bool blocksLaser)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        EntityUid hit = default;
        EntityUid unharmed = default;
        float beamDamage = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x <= 6; x++)
                maps.SetTile(grid, new Vector2i(x, 0), floor);

            var origin = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
            var field = entities.SpawnEntity(prototype, new EntityCoordinates(grid, new Vector2(3.5f, 0.5f)));
            var target = entities.SpawnEntity("MobHuman", new EntityCoordinates(grid, new Vector2(6.5f, 0.5f)));
            entities.AddComponent<PressureImmunityComponent>(target);
            hit = blocksLaser ? field : target;
            unharmed = blocksLaser ? target : field;
            var gun = entities.SpawnEntity("WeaponLaserPistolNF", origin);
            var ray = entities.SpawnEntity("RedLaser", origin);
            var trace = new HitscanTraceEvent
            {
                FromCoordinates = origin,
                ShotDirection = Vector2.UnitX,
                Gun = gun,
                Shooter = gun,
                Target = target,
            };
            entities.EventBus.RaiseLocalEvent(ray, ref trace);
            beamDamage = entities.GetComponent<DamageableComponent>(hit).Damage["Heat"].Float();
            Assert.That(beamDamage, Is.GreaterThan(0));

            var bolt = entities.SpawnEntity("BulletLaser", origin);
            entities.System<GunSystem>().ShootProjectile(bolt, Vector2.UnitX, Vector2.Zero, gun);
        });

        await pair.RunTicksSync(24);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<DamageableComponent>(hit).Damage["Heat"].Float(), Is.GreaterThan(beamDamage));
            Assert.That(entities.GetComponent<DamageableComponent>(unharmed).Damage["Heat"].Float(), Is.Zero);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RangedNpcShootsCoverAndPrefersAnExposedHostile()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        EntityUid npc = default;
        EntityUid field = default;
        EntityUid target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CCVars.NPCEnabled, true);
            config.SetCVar(CCVars.NPCPathfinding, true);
            config.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false);
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var gravity = entities.EnsureComponent<GravityComponent>(grid);
            gravity.Inherent = true;
            gravity.Enabled = true;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = -1; x <= 7; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    maps.SetTile(grid, new Vector2i(x, y), floor);
                    if (y != 0)
                        entities.SpawnEntity("WallSolid", new EntityCoordinates(grid, new Vector2(x + 0.5f, y + 0.5f)));
                }
            }

            npc = entities.SpawnEntity("MobSyndicateNavalCommanderA", new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
            var fieldCoordinates = new EntityCoordinates(grid, new Vector2(3.5f, 0.5f));
            var tacticalField = entities.SpawnEntity("HolosignForcefieldTSF", fieldCoordinates);
            target = entities.SpawnEntity("MobHuman", new EntityCoordinates(grid, new Vector2(6.5f, 0.5f)));
            var factions = entities.System<NpcFactionSystem>();
            factions.ClearFactions(npc);
            factions.AddFaction(npc, "Syndicate");
            factions.ClearFactions(target);
            factions.AddFaction(target, "NanoTrasen");

            var blackboard = entities.GetComponent<HTNComponent>(npc).Blackboard;
            var utility = entities.System<NPCUtilitySystem>();
            Assert.That(utility.GetEntities(blackboard, "NearbyGunTargets").GetHighest(), Is.EqualTo(target));
            entities.DeleteEntity(tacticalField);

            field = entities.SpawnEntity("HolosignForcefield", fieldCoordinates);
            Assert.That(utility.GetEntities(blackboard, "NearbyGunTargets").GetHighest(), Is.EqualTo(field));

            var exposed = entities.SpawnEntity("MobHuman", new EntityCoordinates(grid, new Vector2(2.5f, 0.5f)));
            factions.ClearFactions(exposed);
            factions.AddFaction(exposed, "NanoTrasen");
            Assert.That(utility.GetEntities(blackboard, "NearbyGunTargets").GetHighest(), Is.EqualTo(exposed));
            entities.DeleteEntity(exposed);
        });

        var fieldHit = false;
        for (var i = 0; i < 30 && !fieldHit; i++)
        {
            await pair.RunTicksSync(10);
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                fieldHit = entities.GetComponent<DamageableComponent>(field).TotalDamage.Float() > 0f;
            });
        }

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(fieldHit, Is.True, "The NPC must shoot the field even in a corridor it cannot cross.");
            Assert.That(entities.GetComponent<NPCRangedCombatComponent>(npc).Target, Is.EqualTo(field));
            Assert.That(entities.GetComponent<DamageableComponent>(target).Damage["Piercing"].Float(), Is.Zero);
        });
        await pair.CleanReturnAsync();
    }
}
