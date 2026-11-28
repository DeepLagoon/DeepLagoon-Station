using System.Threading.Tasks;
using Content.Server._DeepLagoon.BodyInsurance;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BodyInsuranceSectorCompatibilityTests
{
    [Test]
    public async Task ExpeditionDepartureCannotBagOrChargeALivingInsuredCharacter()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var body = entities.SpawnEntity("MobHumanDummy", map.GridCoords);
            var policy = entities.EnsureComponent<BodyInsuranceComponent>(body);
            policy.Insured = true;
            var original = entities.GetComponent<TransformComponent>(body).Coordinates;
            Assert.That(entities.System<BodyInsuranceSystem>().TryExpeditionRescue(body, map.Grid, map.GridCoords), Is.False);
            Assert.That(policy.Insured, Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(original));
        });
        await pair.CleanReturnAsync();
    }
}
