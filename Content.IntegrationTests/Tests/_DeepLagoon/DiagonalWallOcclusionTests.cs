using System.Collections.Generic;
using System.Numerics;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Weapons.Melee;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DiagonalWallOcclusionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task DiagonalWallsOnlyOccludeTheirSolidHalf(bool client)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var entities = client ? pair.Client.EntMan : pair.Server.EntMan;
        var walls = new List<EntityUid>();
        EntityUid map = default;
        EntityUid grid = default;

        void Setup()
        {
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out var mapId);
            var gridEntity = maps.CreateGridEntity(mapId);
            grid = gridEntity.Owner;
            var definitions = client
                ? pair.Client.ResolveDependency<ITileDefinitionManager>()
                : pair.Server.ResolveDependency<ITileDefinitionManager>();
            var tile = new Tile(definitions["Plating"].TileId);
            var index = 0;
            foreach (var prototype in new[]
                     {
                         "WallShuttleDiagonal", "WallSolidDiagonal", "WallReinforcedDiagonal", "WallMiningDiagonal",
                         "WallPlastitaniumDiagonal", "WallPlastitaniumDiagonalIndestructible", "WallXenoborgDiagonal",
                         "WallWoodDiagonal", "WallUraniumDiagonal",
                         "WallReinforcedDiagonalOutpost", "WallMiningDiagonalOutpost",
                         "WallPlastitaniumDiagonalOutpost", "WallPlastitaniumDiagonalCapital", "WallNanolaminateDiagonal",
                     })
            foreach (var degrees in new[] { 0, 90, 180, 270 })
            {
                var tilePos = new Vector2i(index++ * 4, 0);
                maps.SetTile(gridEntity, tilePos, tile);
                var wall = entities.SpawnEntity(prototype, new EntityCoordinates(grid, tilePos + new Vector2(.5f)));
                entities.System<SharedTransformSystem>().SetLocalRotation(wall, Angle.FromDegrees(degrees));
                walls.Add(wall);
            }
        }

        void Check()
        {
            var transforms = entities.System<SharedTransformSystem>();
            var occlusion = entities.System<OccluderSystem>();
            var fixtures = entities.System<FixtureSystem>();
            foreach (var wall in walls)
            {
                var occluder = entities.GetComponent<OccluderComponent>(wall);
                var xform = entities.GetComponent<TransformComponent>(wall);
                var (position, rotation) = transforms.GetWorldPositionRotation(xform);
                MapCoordinates Point(float x, float y) => new(position + rotation.RotateVec(new Vector2(x, y)), xform.MapID);
                var insideFreeHalf = Point(-.25f, .25f);
                var context = $"{entities.GetComponent<MetaDataComponent>(wall).EntityPrototype!.ID}, rotation={rotation}";

                Assert.Multiple(() =>
                {
                    Assert.That(occluder.Enabled, Is.True, context);
                    Assert.That(occlusion.ContainsPoint(occluder, xform, insideFreeHalf.Position), Is.False,
                        $"The walkable half must not enclose the eye: {context}");
                    Assert.That(occlusion.ContainsPoint(occluder, xform, Point(.25f, -.25f).Position), Is.True, context);
                    // The renderer builds its FOV mask from this polygon. It must cover exactly
                    // the physical wall, including when the entity or its grid is rotated.
                    var shape = entities.GetComponent<FixturesComponent>(wall).Fixtures["fix1"].Shape;
                    var physicsTransform = new Robust.Shared.Physics.Transform(position, rotation);
                    foreach (var x in new[] { -.4f, -.2f, .2f, .4f })
                    foreach (var y in new[] { -.4f, -.2f, .2f, .4f })
                    {
                        if (x == y) // Avoid points exactly on the diagonal edge.
                            continue;
                        var point = Point(x, y).Position;
                        Assert.That(occlusion.ContainsPoint(occluder, xform, point),
                            Is.EqualTo(fixtures.TestPoint(shape, physicsTransform, point)), $"{context}, sample=({x}, {y})");
                    }
                });
            }
        }

        if (client)
            await pair.Client.WaitPost(Setup);
        else
            await pair.Server.WaitPost(Setup);

        foreach (var gridDegrees in new[] { 0, 30 })
        {
            void Rotate() => entities.System<SharedTransformSystem>().SetLocalRotation(grid, Angle.FromDegrees(gridDegrees));
            if (client)
                await pair.Client.WaitPost(Rotate);
            else
                await pair.Server.WaitPost(Rotate);
            await pair.RunTicksSync(2);
            if (client)
                await pair.Client.WaitAssertion(Check);
            else
                await pair.Server.WaitAssertion(Check);
        }

        if (client)
            await pair.Client.WaitPost(() => entities.DeleteEntity(map));
        else
            await pair.Server.WaitPost(() => entities.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    [TestCase("Kudzu")]
    [TestCase("FleshBlocker")]
    public async Task GrowthOnADiagonalWallCanBeAttackedFromTheFreeHalf(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        EntityUid wall = default, actor = default, knife = default;
        await pair.Server.WaitAssertion(() =>
        {
            wall = entities.SpawnEntity("WallSolidDiagonal", map.GridCoords);
            actor = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(-.25f, .25f)));
            knife = entities.SpawnEntity("CombatKnife", map.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickup(actor, knife), Is.True);
            entities.System<SharedCombatModeSystem>().SetInCombatMode(actor, true);
        });
        // Selecting a melee weapon starts its normal attack cooldown.
        await pair.RunTicksSync(pair.Server.Timing.TickRate * 2);
        await pair.Server.WaitAssertion(() =>
        {
            entities.System<SharedTransformSystem>().SetCoordinates(actor, map.GridCoords.Offset(new Vector2(-.25f, .25f)));
            var growth = entities.SpawnEntity(prototype, map.GridCoords);
            Assert.That(entities.System<ExamineSystemShared>().CanExamine(actor, growth), Is.True);
            Assert.That(entities.System<SharedInteractionSystem>().InRangeUnobstructed(actor, growth, overlapCheck: false), Is.True);
            var weapon = entities.GetComponent<MeleeWeaponComponent>(knife);
            Assert.That(entities.System<SharedMeleeWeaponSystem>().AttemptLightAttack(actor, knife, weapon, growth), Is.True);
            Assert.That(entities.GetComponent<DamageableComponent>(growth).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(entities.Deleted(wall), Is.False, "Clearing growth must not require removing the wall");
        });
        await pair.CleanReturnAsync();
    }
}
