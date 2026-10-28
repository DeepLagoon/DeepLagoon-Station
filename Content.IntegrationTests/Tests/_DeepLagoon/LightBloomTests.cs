using System.Reflection;
using System.Linq;
using Content.Client._DeepLagoon.LightBloom;
using Content.Shared.CCVar;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LightBloomTests
{
    [Test]
    public async Task LampLayersRestoreWithoutChangingLightOrAnimationVisibility()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity("AlwaysPoweredWallLight", MapCoordinates.Nullspace);
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var light = entities.GetComponent<PointLightComponent>(uid);
            var system = entities.System<LightBloomSystem>();
            void Apply(bool visible) => typeof(LightBloomSystem).GetMethod("ApplyLampVisibility", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(system, [uid, sprite, visible]);
            var layers = sprite.AllLayers.Cast<SpriteComponent.Layer>().ToArray();
            var originals = layers.Select(l => (l.Shader, l.ShaderPrototype, l.Visible)).ToArray();
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.LightBloomEnabled, false);
            Apply(false);
            Assert.That(layers.All(l => l.Shader != null), Is.True);
            Assert.That(light.Enabled, Is.True);
            Apply(true);
            for (var i = 0; i < layers.Length; i++)
            {
                Assert.That(layers[i].Shader, Is.SameAs(originals[i].Shader));
                Assert.That(layers[i].ShaderPrototype, Is.EqualTo(originals[i].ShaderPrototype));
                Assert.That(layers[i].Visible, Is.EqualTo(originals[i].Visible));
            }
            // A second viewport or the next frame must be able to hide and restore the fixture again.
            Apply(false);
            Apply(true);
            Assert.That(layers.Select(l => l.Visible), Is.EqualTo(originals.Select(l => l.Visible)));
            var prototype = pair.Client.ResolveDependency<IPrototypeManager>().Index<ShaderPrototype>("LightBloom");
            using var shader = prototype.InstanceUnique();
            Assert.That(shader, Is.Not.Null);
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }
}
