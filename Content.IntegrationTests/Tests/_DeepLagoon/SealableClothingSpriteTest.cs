using System.Linq;
using Content.Client._Goobstation.Clothing.Components;
using Content.Shared.Clothing.Components;
using Content.Shared.Prototypes;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SealableClothingSpriteTest
{
    [Test]
    public async Task AllSealableClothingLayersHaveExistingStates()
    {
        await using var pair = await PoolManager.GetServerClient();
        var client = pair.Client;
        var prototypes = client.ResolveDependency<IPrototypeManager>();
        var factory = client.ResolveDependency<IComponentFactory>();
        var cache = client.ResolveDependency<IResourceCache>();

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>()
                             .Where(p => !p.Abstract && !pair.IsTestPrototype(p)))
                {
                    if (!prototype.TryComp<SealableClothingVisualsComponent>(out var sealedVisuals, factory))
                        continue;

                    Assert.That(prototype.TryComp<SpriteComponent>(out var sprite, factory), Is.True, prototype.ID);
                    Assert.That(prototype.TryComp<ClothingComponent>(out var clothing, factory), Is.True, prototype.ID);

                    foreach (var visuals in new[] { clothing.ClothingVisuals, sealedVisuals.ClothingVisuals })
                    foreach (var (slot, layers) in visuals)
                    foreach (var layer in layers)
                    {
                        if (layer.State == null || layer.TexturePath != null)
                            continue;

                        var rsi = layer.RsiPath == null
                            ? sprite.BaseRSI
                            : cache.GetResource<RSIResource>(SpriteSpecifierSerializer.TextureRoot / layer.RsiPath).RSI;
                        Assert.That(rsi, Is.Not.Null, $"{prototype.ID}/{slot} has no RSI.");
                        Assert.That(rsi.TryGetState(layer.State, out _), Is.True,
                            $"{prototype.ID}/{slot}: {rsi.Path} is missing {layer.State}.");
                    }
                }
            });
        });

        await pair.CleanReturnAsync();
    }
}
