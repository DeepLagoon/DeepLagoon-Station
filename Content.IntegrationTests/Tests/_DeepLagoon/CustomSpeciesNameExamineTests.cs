using Content.Server.Examine;
using Content.Server.Station.Systems;
using Content.Shared.Preferences;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class CustomSpeciesNameExamineTests
{
    [Test]
    public async Task ExamineUsesCustomSpeciesNameInPlaceOfDefault()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid observer = default;
        EntityUid custom = default;

        await pair.Server.WaitAssertion(() =>
        {
            var spawning = pair.Server.EntMan.System<StationSpawningSystem>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithAge(40)
                .WithGender(Gender.Female);
            observer = spawning.SpawnPlayerMob(map.GridCoords, null, profile, null);
            custom = spawning.SpawnPlayerMob(map.GridCoords, null, profile.WithCustomSpeciesName("Лунная лисица"), null);
        });

        // The identity system updates the pronouns on the tick after spawning.
        await pair.RunTicksSync(1);
        await pair.Server.WaitAssertion(() =>
        {
            var examine = pair.Server.EntMan.System<ExamineSystem>();
            Assert.Multiple(() =>
            {
                Assert.That(examine.GetExamineText(custom, observer).ToString(), Does.Contain("Она Лунная лисица среднего возраста."));
                Assert.That(examine.GetExamineText(custom, custom).ToString(), Does.Contain("Вы - Лунная лисица среднего возраста."));
                Assert.That(examine.GetExamineText(observer, custom).ToString(), Does.Contain("Она человек среднего возраста."));
            });
        });

        await pair.CleanReturnAsync();
    }
}
