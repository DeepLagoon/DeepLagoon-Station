using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Content.Server._DeepLagoon.Art;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Art;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Client.GameObjects;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;
using UserInterfaceSystem = Robust.Server.GameObjects.UserInterfaceSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ArtIntegrationTests
{
    [Test]
    public async Task CanvasRequiresHeldToolReplicatesPreviewAndArchivesOnSave()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, NoLoadTestPrototypes = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        EntityUid user = default, other = default, canvas = default, brush = default, frame = default, gallery = default;
        ArtDocument.Baked baked = null;
        var leaseRevision = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var mapSystem = entities.System<SharedMapSystem>();
            mapSystem.SetTile(map.Grid, map.Grid.Comp, map.GridCoords.Offset(new Vector2(1, 0)), map.Tile.Tile);
            mapSystem.SetTile(map.Grid, map.Grid.Comp, map.GridCoords.Offset(new Vector2(0, 1)), map.Tile.Tile);
            var serializer = pair.Server.ResolveDependency<IRobustSerializer>();
            using var stream = new MemoryStream();
            var state = new ArtEditorState(ArtLimits.EmptyDocument, 7, "chalk", "", 1);
            serializer.Serialize(stream, state); stream.Position = 0;
            var read = serializer.Deserialize<ArtEditorState>(stream);
            Assert.That(read.Document, Is.EqualTo(state.Document)); Assert.That(read.Revision, Is.EqualTo(7));
            baked = ArtDocument.Bake(JsonSerializer.Serialize(new { version = 1, title = "Тест", layers = new[] {
                new { name = "Чернила", visible = true, opacity = 1, png = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create(), mask = "" } } }));
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            other = entities.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            brush = entities.SpawnEntity("DLArtBrush", map.GridCoords);
            canvas = entities.SpawnEntity("DLArtCanvas", map.GridCoords);
            var hands = entities.System<SharedHandsSystem>();
            InteractUsingEvent Open() => new(user, brush, canvas, map.GridCoords);
            var invalid = Open(); entities.EventBus.RaiseLocalEvent(canvas, invalid); Assert.That(invalid.Handled, Is.False);
            Assert.That(hands.TryPickupAnyHand(user, brush, animate: false), Is.True);
            var valid = Open(); entities.EventBus.RaiseLocalEvent(canvas, valid); Assert.That(valid.Handled, Is.True);
            var data = entities.GetComponent<ArtCanvasComponent>(canvas); Assert.That(data.Artist, Is.EqualTo(user));
            leaseRevision = data.Revision;
            void Message(string action, string text, EntityUid actor, int index = 0, int count = 1, int revision = -1) =>
                entities.EventBus.RaiseLocalEvent(canvas, new ArtEditorMessage(action, text, revision < 0 ? leaseRevision : revision, index, count) { Actor = actor, UiKey = ArtUiKey.Key });
            Message("preview", baked.Preview, other);
            Assert.That(entities.GetComponent<ArtDisplayComponent>(canvas).Preview, Is.Empty);
            var count = (baked.Preview.Length + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars;
            for (var i = 0; i < count; i++) Message("preview", baked.Preview.Substring(i * ArtLimits.ChunkChars,
                Math.Min(ArtLimits.ChunkChars, baked.Preview.Length - i * ArtLimits.ChunkChars)), user, i, count);
            Assert.That(entities.GetComponent<ArtDisplayComponent>(canvas).Preview, Is.Not.Empty);
            Assert.That(data.Document, Is.EqualTo(ArtLimits.EmptyDocument), "Live previews must not commit the document or archive it");
            Message("save", baked.Json, other); Assert.That(data.Revision, Is.EqualTo(leaseRevision));
            count = (baked.Json.Length + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars;
            for (var i = 0; i < count; i++) Message("save", baked.Json.Substring(i * ArtLimits.ChunkChars,
                Math.Min(ArtLimits.ChunkChars, baked.Json.Length - i * ArtLimits.ChunkChars)), user, i, count);
        });
        var complete = false;
        for (var i = 0; i < 150 && !complete; i++)
        {
            await pair.RunTicksSync(5);
            await pair.Server.WaitPost(() => complete = entities.System<UserInterfaceSystem>().TryGetUiState<ArtEditorState>(canvas, ArtUiKey.Key, out var savedState) && savedState.Result > 0);
            if (!complete) await Task.Delay(20);
        }
        Assert.That(complete, Is.True, "Validated save did not finish");
        Assert.That(await db.GetArtworkPoolAsync(), Has.Count.EqualTo(1), "Saving must archive the painting before it is framed");
        var savedWork = await db.GetArtworkAsync((await db.GetArtworkPoolAsync()).Single());
        Assert.That(savedWork.Document, Is.EqualTo(baked.Json));
        Assert.That(savedWork.Title, Is.EqualTo("Тест"));
        await pair.Server.WaitAssertion(() =>
        {
            var data = entities.GetComponent<ArtCanvasComponent>(canvas);
            Assert.That(data.Document, Is.EqualTo(baked.Json)); Assert.That(data.SavedPreview, Is.EqualTo(baked.Preview));
            var hands = entities.System<SharedHandsSystem>();
            entities.System<UserInterfaceSystem>().CloseUis(canvas);
            Assert.That(data.Artist, Is.Null);
            Assert.That(hands.TryDrop(user, brush), Is.True);
            foreach (var prototype in new[] { "CrayonRed", "DLArtSprayCan" })
            {
                var tool = entities.SpawnEntity(prototype, map.GridCoords);
                Assert.That(hands.TryPickupAnyHand(user, tool, animate: false), Is.True);
                var open = new InteractUsingEvent(user, tool, canvas, map.GridCoords);
                entities.EventBus.RaiseLocalEvent(canvas, open);
                Assert.That(open.Handled, Is.True, prototype + " must open the canvas editor");
                Assert.That(data.Tool, Is.EqualTo(tool));
                entities.System<UserInterfaceSystem>().CloseUis(canvas);
                Assert.That(hands.TryDrop(user, tool), Is.True);
            }
            var wallCoords = map.GridCoords.Offset(new Vector2(1, 0));
            var wall = entities.SpawnEntity("WallSolid", wallCoords);
            var frameItem = entities.SpawnEntity("DLArtFrameItem", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, frameItem, animate: false), Is.True);
            var install = new AfterInteractEvent(user, frameItem, wall, wallCoords, true);
            entities.EventBus.RaiseLocalEvent(frameItem, install); Assert.That(install.Handled, Is.True);
            frame = entities.EntityQuery<ArtFrameComponent>().Single().Owner;
            Assert.That(entities.GetComponent<TransformComponent>(frame).Anchored, Is.True);
            Assert.That(hands.TryPickupAnyHand(user, canvas, animate: false), Is.True);
            var insert = new InteractUsingEvent(user, canvas, frame, wallCoords);
            entities.EventBus.RaiseLocalEvent(frame, insert); Assert.That(insert.Handled, Is.True);
        });
        await pair.RunTicksSync(30);
        var pool = await db.GetArtworkPoolAsync(); Assert.That(pool, Has.Count.EqualTo(1));
        var work = await db.GetArtworkAsync(pool.Single()); Assert.That(work.Document, Is.EqualTo(baked.Json));
        await pair.Server.WaitAssertion(() =>
        {
            var slot = entities.System<SharedContainerSystem>().EnsureContainer<ContainerSlot>(frame, "art-canvas");
            Assert.That(slot.ContainedEntity, Is.EqualTo(canvas));
            gallery = entities.SpawnEntity("DLArtGalleryFrame", map.GridCoords.Offset(new Vector2(0, 1)));
        });
        complete = false;
        for (var i = 0; i < 150 && !complete; i++)
        {
            await pair.RunTicksSync(5);
            await pair.Server.WaitPost(() => complete = entities.System<SharedContainerSystem>().EnsureContainer<ContainerSlot>(gallery, "art-canvas").ContainedEntity != null);
            if (!complete) await Task.Delay(20);
        }
        Assert.That(complete, Is.True, "Gallery frame did not restore the archived canvas");
        await pair.RunTicksSync(30);
        var netFrame = entities.GetNetEntity(frame);
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan; var uid = client.GetEntity(netFrame);
            Assert.That(client.GetComponent<ArtDisplayComponent>(uid).Preview, Is.EqualTo(baked.Preview));
            var sprite = client.GetComponent<SpriteComponent>(uid);
            Assert.That(sprite.LayerMapTryGet("art-image", out var layer), Is.True);
            Assert.That(sprite[layer].Visible, Is.True, "The artwork must be an actual world sprite layer");
        });
        Assert.That(await db.GetArtworkPoolAsync(), Has.Count.EqualTo(1), "Restoring a gallery copy must not rearchive it");
        await pair.CleanReturnAsync();
    }
}
