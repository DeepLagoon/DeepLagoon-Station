using System.Collections.Generic;
using System.Linq;
using Content.Client.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SpriteCardinalBoundsTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: CardinalFireBoundsProbe
  components:
  - type: Sprite
    sprite: Structures/Machines/VendingMachines/empty.rsi
    state: normal-unshaded
    snapCardinals: true
  - type: Appearance
  - type: FireVisuals
    sprite: Effects/fire.rsi
    normalState: '1'
    alternateState: '2'
";

    [TestCase(1)]
    [TestCase(4)]
    public async Task FireOnCardinalSnappingSpriteHasValidBounds(int stacks)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity("CardinalFireBoundsProbe", MapCoordinates.Nullspace);
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var appearance = entities.System<AppearanceSystem>();
            appearance.SetData(uid, FireVisuals.OnFire, true);
            appearance.SetData(uid, FireVisuals.FireStacks, stacks);
            appearance.FrameUpdate(0);
            var layer = sprite.LayerData[sprite.LayerMapGet(FireVisualLayers.Fire)];
            Assert.That(layer.ActualState!.RsiDirections, Is.EqualTo(RsiDirectionType.Dir4));
            Assert.That(sprite.SnapCardinals, Is.True, "The machine's own rendering must retain cardinal snapping");
            Assert.DoesNotThrow(() => entities.System<SpriteSystem>().GetLocalBounds((uid, sprite)));
            appearance.SetData(uid, FireVisuals.OnFire, false);
            appearance.FrameUpdate(0);
            Assert.DoesNotThrow(() => entities.System<SpriteSystem>().GetLocalBounds((uid, sprite)));
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CardinalSnappingLayersUseSingleDirectionStates()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var factory = pair.Client.EntMan.ComponentFactory;
            var errors = new List<string>();
            var checkedLayers = 0;
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>().Where(p => !p.Abstract && !pair.IsTestPrototype(p)))
            {
                if (!prototype.TryComp<SpriteComponent>(out var sprite, factory)) continue;
                for (var i = 0; i < sprite.LayerData.Count; i++)
                {
                    var layer = sprite.LayerData[i];
                    var snaps = sprite.SnapCardinals;
                    if (sprite.GranularLayersRendering && layer.RenderingStrategy != LayerRenderingStrategy.UseSpriteStrategy)
                        snaps = layer.RenderingStrategy == LayerRenderingStrategy.SnapToCardinals;
                    if (snaps && layer.ActualState != null) checkedLayers++;
                    if (snaps && layer.ActualState is { RsiDirections: not RsiDirectionType.Dir1 } state)
                        errors.Add($"{prototype.ID}, layer {i}, {layer.ActualRsi?.Path}/{layer.State}: {state.RsiDirections}");
                }
            }
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(checkedLayers, Is.GreaterThan(0));
        });
        await pair.CleanReturnAsync();
    }
}
