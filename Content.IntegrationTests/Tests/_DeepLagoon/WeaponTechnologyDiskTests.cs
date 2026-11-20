using System.Linq;
using Content.Server.Lathe;
using Content.Server.Research.Systems;
using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using Content.Shared.Research.TechnologyDisk.Components;
using Content.Shared.Research.TechnologyDisk.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class WeaponTechnologyDiskTests
{
    [TestCase("MercenaryTechFab")]
    [TestCase("MercenaryTechFabHacked")]
    [TestCase("FactionLathe")]
    public async Task WeaponDisksUnlockWeaponsAndAmmoOnLinkedLathes(string latheId)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var research = entities.System<ResearchSystem>();
            var lathe = entities.System<LatheSystem>();
            var disks = entities.System<TechnologyDiskSystem>();
            string[] diskIds =
            [
                "TechDiskEnergyWeapons",
                "TechDiskTSFAnnie",
                "TechDiskTSFLecter",
                "TechDiskTSFDrozd",
                "TechDiskTSFBandit",
                "TechDiskTSFICWS",
                "TechDiskC20r",
                "TechDiskM90",
                "TechDiskBulldog",
                "TechDiskHristov",
                "TechDiskMla73",
            ];

            foreach (var diskId in diskIds)
            {
                var server = entities.SpawnEntity("ResearchAndDevelopmentServerMerc", MapCoordinates.Nullspace);
                var printer = entities.SpawnEntity(latheId, MapCoordinates.Nullspace);
                var disk = entities.SpawnEntity(diskId, MapCoordinates.Nullspace);
                var component = entities.GetComponent<TechnologyDiskComponent>(disk);
                var recipes = component.Recipes!.ToArray();
                research.RegisterClient(printer, server);

                Assert.That(recipes, Is.Not.Empty, diskId);
                Assert.That(lathe.TryGetAvailableRecipes(printer, out var before), Is.True);
                Assert.That(before, Does.Not.Contain(recipes[0]), $"{latheId}: {diskId} weapon must require an unlock");

                Assert.That(disks.TryUpload((disk, component), server, null), Is.True, diskId);
                var database = entities.GetComponent<TechnologyDatabaseComponent>(server);
                Assert.That(recipes.Except(database.UnlockedRecipes), Is.Empty, $"{diskId}: server upload");
                Assert.That(lathe.TryGetAvailableRecipes(printer, out var after), Is.True);
                Assert.That(recipes.Except(after!), Is.Empty, $"{latheId}: {diskId} must unlock every advertised recipe");

                entities.DeleteEntity(printer);
                entities.DeleteEntity(server);
            }
        });
        await pair.CleanReturnAsync();
    }
}
