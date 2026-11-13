using System.Collections.Generic;
using System.Linq;
using Content.Server.Lathe;
using Content.Server.Power.Components;
using Content.Server.Research.Systems;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared.Interaction;
using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ShipArmamentTests
{
    [Test]
    public async Task ServerTypeAndPrerequisitesControlRecipeAccess()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var research = server.System<ResearchSystem>();
        var lathe = server.System<LatheSystem>();
        var entities = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var console = entities.SpawnEntity("ComputerResearchAndDevelopment", map.GridCoords);
            var press = entities.SpawnEntity("IndustrialPressEconomyPlates", map.GridCoords);
            var actor = entities.SpawnEntity(null, map.GridCoords);

            var serverTypes = new (string Id, bool Faction, bool Phaethon, bool Tsfmc)[]
            {
                ("ResearchAndDevelopmentServer", false, false, false),
                ("ResearchAndDevelopmentServerMerc", true, false, false),
                ("ResearchAndDevelopmentServerRogues", true, true, false),
                ("ResearchAndDevelopmentServerTSFMC", true, false, true),
                ("ResearchAndDevelopmentServerMMC", true, false, false),
                ("ResearchAndDevelopmentServerUssp", true, false, false),
                ("ResearchAndDevelopmentServerDebug", true, true, true),
            };

            foreach (var (serverId, faction, phaethon, tsfmc) in serverTypes)
            {
                var rdServer = entities.SpawnEntity(serverId, map.GridCoords);
                entities.GetComponent<ResearchServerComponent>(rdServer).Points = 1_000_000;
                research.RegisterClient(console, rdServer);
                research.RegisterClient(press, rdServer);
                var database = entities.GetComponent<TechnologyDatabaseComponent>(console);

                bool Unlock(string technology) => research.UnlockTechnology(console, technology, actor);
                bool Visible(TechnologyPrototype technology) =>
                    technology.GetAllDisciplines().Any(database.SupportedDisciplines.Contains);

                var medium = server.ProtoMan.Index<TechnologyPrototype>("DLShipArmamentMedium");
                var heavy = server.ProtoMan.Index<TechnologyPrototype>("DLShipArmamentHeavy");

                Assert.That(Visible(medium), Is.EqualTo(faction), serverId);
                Assert.That(Visible(heavy), Is.EqualTo(faction), serverId);
                Assert.That(Unlock(medium.ID), Is.False, serverId);
                Assert.That(Unlock(heavy.ID), Is.False, serverId);
                Assert.That(Unlock("DLShipMountsAdvanced"), Is.False, serverId);
                Assert.That(Unlock("DLShipMountsBasic"), Is.False, serverId);
                Assert.That(Unlock("UniversalMaterialResearchT1"), Is.True, serverId);
                Assert.That(Unlock("DLShipMountsBasic"), Is.False, serverId);
                Assert.That(Unlock("UniversalMaterialResearchT2"), Is.True, serverId);

                Assert.That(Unlock("DLShipMountsBasic"), Is.True, serverId);
                Assert.That(Unlock("DLShipArmamentLight"), Is.True, serverId);
                Assert.That(Unlock("DLShipMountsAdvanced"), Is.True, serverId);
                Assert.That(Unlock("DLShipMountsMassive"), Is.True, serverId);
                Assert.That(Unlock(medium.ID), Is.EqualTo(faction), serverId);
                Assert.That(Unlock(heavy.ID), Is.EqualTo(faction), serverId);
                Assert.That(Unlock("DLShipArmamentSyndicateCruiser"), Is.False, serverId);
                Assert.That(Unlock("DLShipArmamentTsfmcCruiser"), Is.False, serverId);
                Assert.That(Unlock("DLShipMountsCapital"), Is.True, serverId);

                var factionTechnologies = new (string Id, bool Accessible)[]
                {
                    ("DLShipArmamentSyndicateLight", phaethon),
                    ("DLShipArmamentSyndicateMissile", phaethon),
                    ("DLShipArmamentSyndicateHeavy", phaethon),
                    ("DLShipArmamentSyndicateCruiser", phaethon),
                    ("DLShipArmamentTsfmcStandard", tsfmc),
                    ("DLShipArmamentTsfmcCruiser", tsfmc),
                };
                foreach (var (id, accessible) in factionTechnologies)
                {
                    var technology = server.ProtoMan.Index<TechnologyPrototype>(id);
                    Assert.That(Visible(technology), Is.EqualTo(accessible), $"{serverId}: {id}");
                    Assert.That(Unlock(id), Is.EqualTo(accessible), $"{serverId}: {id}");
                }

                Assert.That(lathe.TryGetAvailableRecipes(press, out var recipes), Is.True);
                Assert.That(recipes!.Contains("DLShipAssemblyWeaponTurretAK570"), Is.EqualTo(faction), serverId);
                Assert.That(recipes.Contains("DLShipAssemblyWeaponTurretCerberus"), Is.EqualTo(faction), serverId);
                Assert.That(recipes.Contains("DLShipAssemblyHardpointEnergySuperheavy"), Is.True, serverId);
                foreach (var (id, accessible) in factionTechnologies)
                {
                    var technology = server.ProtoMan.Index<TechnologyPrototype>(id);
                    foreach (var recipe in technology.RecipeUnlocks)
                        Assert.That(recipes.Contains(recipe), Is.EqualTo(accessible), $"{serverId}: {recipe}");
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ResearchedKitsUseFinishedPartsAndDeployOnCompatibleHardpoints()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var research = server.System<ResearchSystem>();
        var lathe = server.System<LatheSystem>();
        var storage = server.System<SharedEntityStorageSystem>();
        var inputs = new List<EntityUid>();
        EntityCoordinates At(int x) => new(map.Grid.Owner, x + 0.5f, 0.5f);
        EntityUid press = default;
        EntityUid actor = default;
        EntityUid mountKit = default;
        var mountRecipe = server.ProtoMan.Index<LatheRecipePrototype>("DLShipAssemblyHardpointBallisticSuperlight");
        var flareRecipe = server.ProtoMan.Index<LatheRecipePrototype>("DLShipAssemblyWeaponTurretFlare");

        await server.WaitAssertion(() =>
        {
            for (var x = 0; x <= 6; x++)
                maps.SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);

            var rdServer = entities.SpawnEntity("ResearchAndDevelopmentServer", At(0));
            press = entities.SpawnEntity("IndustrialPressEconomyPlates", At(1));
            actor = entities.SpawnEntity("MobHuman", At(4));
            research.RegisterClient(press, rdServer);
            Assert.That(lathe.TryGetAvailableRecipes(press, out var startingRecipes), Is.True);
            Assert.That(startingRecipes!.Contains(mountRecipe.ID), Is.False);

            research.AddTechnology(rdServer, "DLShipMountsBasic");
            research.AddTechnology(rdServer, "DLShipArmamentLight");
            mountKit = Fabricate(mountRecipe);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var flareKit = Fabricate(flareRecipe);
            var wrongMount = entities.SpawnEntity("HardpointEnergyLight", At(5));
            UseKit(flareKit, wrongMount, 5);
            Assert.That(entities.GetComponent<HardpointComponent>(wrongMount).anchoring, Is.Null);

            UseKit(mountKit, null, 3);
            var mount = entities.EntityQuery<HardpointComponent>().Single(m => m.Owner != wrongMount);
            Assert.That(entities.GetComponent<TransformComponent>(mount.Owner).Anchored, Is.True);
            UseKit(flareKit, mount.Owner, 3);
            Assert.That(mount.anchoring, Is.Not.Null);
            Assert.That(entities.GetComponent<MetaDataComponent>(mount.anchoring!.Value).EntityPrototype!.ID, Is.EqualTo("WeaponTurretFlare"));

            var spareFlareKit = entities.SpawnEntity(flareRecipe.Result!.Value, At(4));
            UseKit(spareFlareKit, mount.Owner, 3);
            inputs.Add(mountKit);
            inputs.Add(flareKit);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            foreach (var uid in inputs)
                Assert.That(entities.EntityExists(uid), Is.False, uid.ToString());
            Assert.That(entities.EntityQuery<MetaDataComponent>().Count(m =>
                m.EntityPrototype?.ID == "DLShipAssemblyKitWeaponTurretFlare"), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();

        EntityUid Fabricate(LatheRecipePrototype recipe)
        {
            Assert.That(lathe.CanProduce(press, recipe), Is.False, recipe.ID);
            foreach (var (ingredient, count) in recipe.Entities)
            {
                for (var i = 0; i < count; i++)
                {
                    var part = entities.SpawnEntity(ingredient, At(2));
                    inputs.Add(part);
                    Assert.That(storage.AddToContents(part, press), Is.True, ingredient.Id);
                }
            }

            entities.GetComponent<ApcPowerReceiverComponent>(press).Powered = true;
            Assert.That(lathe.TryAddToQueue(press, recipe, 1), Is.True);
            Assert.That(lathe.TryStartProducing(press), Is.True);
            lathe.FinishProducing(press);
            return entities.EntityQuery<MetaDataComponent>()
                .Single(m => m.EntityPrototype?.ID == recipe.Result!.Value.Id).Owner;
        }

        void UseKit(EntityUid kit, EntityUid? target, int x)
        {
            entities.EventBus.RaiseLocalEvent(kit, new AfterInteractEvent(actor, kit, target, At(x), canReach: true));
        }
    }
}
