using System.Linq;
using Content.Shared.Doors.Components;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunDoorVisualsTests
{
    [Test]
    public async Task ImportedDoorsKeepValidSpritesWhenOpenedAndClosed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var sprites = entities.System<SpriteSystem>();
            var appearance = entities.System<AppearanceSystem>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var checkedDoors = 0;
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>().Where(p =>
                         p.ID.StartsWith("BitrunMap") && p.Components.ContainsKey("Door")))
            {
                var uid = entities.SpawnEntity(prototype.ID, MapCoordinates.Nullspace);
                var sprite = new Entity<SpriteComponent>(uid, entities.GetComponent<SpriteComponent>(uid));
                var data = new Entity<AppearanceComponent>(uid, entities.GetComponent<AppearanceComponent>(uid));
                var door = entities.GetComponent<DoorComponent>(uid);
                Assert.That(sprites.TryGetLayer(sprite, DoorVisualLayers.Base, out var layer, true), Is.True, prototype.ID);
                Assert.That(sprites.LayerMapTryGet(sprite, DoorVisualLayers.Base, out var baseIndex, true), Is.True, prototype.ID);
                foreach (var state in new[] { door.ClosedSpriteState, door.OpenSpriteState, door.OpeningSpriteState, door.ClosingSpriteState })
                    Assert.That(layer!.ActualRsi.TryGetState(state, out _), Is.True,
                        $"{prototype.ID}: the effective base RSI is missing {state}.");

                foreach (var state in new[] { DoorState.Open, DoorState.Closed, DoorState.Open })
                {
                    appearance.SetData(uid, DoorVisuals.State, state);
                    appearance.OnChangeData(data, sprite.Comp);
                    var current = sprites.LayerGetRsiState(sprite, baseIndex);
                    Assert.That(current.Name, Is.EqualTo(state == DoorState.Open ? door.OpenSpriteState : door.ClosedSpriteState), prototype.ID);
                    Assert.That(layer!.ActualRsi.TryGetState(current, out _), Is.True, prototype.ID);
                }
                checkedDoors++;
                entities.DeleteEntity(uid);
            }
            Assert.That(checkedDoors, Is.GreaterThan(10));
            TestContext.Progress.WriteLine($"Validated open/closed sprites and four animation states for {checkedDoors} imported doors.");
        });
        await pair.CleanReturnAsync();
    }
}
