using System.Linq;
using System.Numerics;
using Content.Shared.Stealth;
using Content.Shared.Storage.EntitySystems;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Sprite;

[TestFixture]
public sealed class StealthVisualTest
{
    private const string OtherPass = "test-pass";
    private static readonly ProtoId<ShaderPrototype> OtherShader = "unshaded";

    [Test]
    public async Task StealthBoxPostShaderFollowsServerState()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid box = default;
        NetEntity boxNet = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            var observer = entities.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), observer);
            box = entities.SpawnEntity("StealthBox", new EntityCoordinates(map.Grid, new Vector2(1.5f, 0.5f)));
            entities.System<SharedStealthSystem>().SetVisibility(box, 0.5f);
            boxNet = entities.GetNetEntity(box);
        });

        SpriteComponent.PostShaderEntry GetStealthPass(SpriteComponent sprite)
        {
            var pass = pair.Client.EntMan.System<SpriteSystem>().GetPostShaders(sprite).Single(shader => shader.Id != OtherPass);
            Assert.Multiple(() =>
            {
                Assert.That(pass.GetScreenTexture, Is.True, "The renderer must supply the viewport background.");
                Assert.That(pass.RaiseShaderEvent, Is.True, "The renderer must request the current visibility for each draw.");
            });
            return pass;
        }

        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.GetEntity(boxNet);
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var pass = GetStealthPass(sprite);

            using var viewport = pair.Client.ResolveDependency<IClyde>().CreateViewport(new Vector2i(320, 240));
            var ev = new BeforePostShaderRenderEvent(pass.Id, pass.Shader, sprite, viewport);
            entities.EventBus.RaiseLocalEvent(uid, ref ev);
            Assert.That(sprite.Color.R, Is.LessThan(1f), "The render event must apply the current visibility.");

            var shader = pair.Client.ResolveDependency<IPrototypeManager>().Index(OtherShader).Instance();
            entities.System<SpriteSystem>().SetPostShader(sprite, new SpriteComponent.PostShaderArgs(OtherPass, shader));
        });

        await pair.Server.WaitPost(() => pair.Server.EntMan.System<SharedEntityStorageSystem>().OpenStorage(box));
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var sprite = entities.GetComponent<SpriteComponent>(entities.GetEntity(boxNet));
            Assert.That(entities.System<SpriteSystem>().GetPostShaders(sprite).Single().Id, Is.EqualTo(OtherPass));
            Assert.That(sprite.Color, Is.EqualTo(Color.White));
        });

        await pair.Server.WaitPost(() => pair.Server.EntMan.System<SharedEntityStorageSystem>().CloseStorage(box));
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var sprite = entities.GetComponent<SpriteComponent>(entities.GetEntity(boxNet));
            GetStealthPass(sprite);
            Assert.That(entities.System<SpriteSystem>().HasPostShader(sprite, OtherPass), Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
