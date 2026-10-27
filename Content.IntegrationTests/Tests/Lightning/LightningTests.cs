using System.Linq;
using System.Numerics;
using Content.Server.Lightning;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Lightning;

[TestFixture]
public sealed class LightningTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  parent: Lightning
  id: LightningRegressionBolt
  components:
  - type: Lightning
    spawnOnHit: LightningRegressionHit
- type: entity
  id: LightningRegressionHit
";

    [Test]
    public async Task DirectStrikeUsesTheResolvedSpawnOnHitPrototype()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var source = entities.SpawnEntity(null, map.GridCoords);
            var target = entities.SpawnEntity(null, map.GridCoords.Offset(new Vector2(2, 0)));
            entities.System<LightningSystem>().ShootLightning(source, target, "LightningRegressionBolt", false);
            Assert.That(entities.EntityQuery<MetaDataComponent>()
                .Count(meta => meta.EntityPrototype?.ID == "LightningRegressionHit"), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }
}
