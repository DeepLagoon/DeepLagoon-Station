using System.Linq;
using Content.Server._DeepLagoon.Art;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Shared._DeepLagoon.Art;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Eui;
using Robust.Shared.Containers;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ArtModerationTests
{
    [Test]
    public async Task AnyActiveAdminCanViewAndRemoveButPlayersAndDeadminsCannot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var db = server.ResolveDependency<IServerDbManager>();
        var admins = server.ResolveDependency<IAdminManager>();
        var entities = server.EntMan;
        await server.WaitPost(() =>
        {
            server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.ConsoleLoginLocal, false);
            admins.ReloadAdmin(pair.Player!);
        });
        await PoolManager.WaitUntil(server, () => !admins.HasAdminFlag(pair.Player!, AdminFlags.None));
        var baked = ArtDocument.Bake(System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1, title = "Moderation test", layers = new[] {
                new { name = "Ink", visible = true, opacity = 1, png = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create(), mask = "" } },
        }));
        var row = new ArchivedArtwork { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), RevisionHash = baked.Hash,
            Document = baked.Json, Preview = baked.Preview, Title = baked.Title, Author = pair.Player!.UserId.UserId,
            AuthorName = "Painter", CreatedAt = DateTime.UtcNow };
        await db.ArchiveArtworkAsync(row);
        ArtModerationEui ui = null!;
        async Task Open()
        {
            await server.WaitPost(() =>
            {
                ui = new ArtModerationEui();
                server.ResolveDependency<EuiManager>().OpenEui(ui, pair.Player!);
            });
        }
        async Task Send(ArtModerationRequest request)
        {
            await server.WaitPost(() => ui.HandleMessage(request));
            await PoolManager.WaitUntil(server, () => !((ArtModerationEuiState) ui.GetNewState()).Loading);
        }
        await Open();
        await Send(new ArtModerationRequest("view", row.Id));
        Assert.That(((ArtModerationEuiState) ui.GetNewState()).Selected, Is.Null);
        await Send(new ArtModerationRequest("delete", row.Id));
        Assert.That(await db.GetArtworkAsync(row.Id), Is.Not.Null);
        await server.WaitPost(ui.Close);

        // No additional permission flags at all: every active administrator may moderate art.
        await db.AddAdminAsync(new Admin { UserId = pair.Player!.UserId.UserId, Flags = new() });
        await server.WaitPost(() => admins.ReloadAdmin(pair.Player!));
        await PoolManager.WaitUntil(server, () => admins.HasAdminFlag(pair.Player!, AdminFlags.None));
        Assert.That(admins.GetAdminData(pair.Player!)!.Flags, Is.EqualTo(AdminFlags.None));
        await Open();
        await PoolManager.WaitUntil(server, () => !((ArtModerationEuiState) ui.GetNewState()).Loading);
        Assert.That(((ArtModerationEuiState) ui.GetNewState()).Entries.Single().Id, Is.EqualTo(row.Id));
        await Send(new ArtModerationRequest("view", row.Id));
        var state = (ArtModerationEuiState) ui.GetNewState();
        Assert.That(state.Selected!.Author, Is.EqualTo(row.Author));
        Assert.That(state.Selected.AuthorName, Is.EqualTo("Painter"));
        Assert.That(state.Image, Is.Not.Empty);
        await server.WaitAssertion(() =>
        {
            using var stream = new System.IO.MemoryStream();
            var serializer = server.ResolveDependency<IRobustSerializer>();
            serializer.Serialize(stream, state); stream.Position = 0;
            Assert.That(serializer.Deserialize<ArtModerationEuiState>(stream).Selected!.Author, Is.EqualTo(row.Author));
        });
        var map = await pair.CreateTestMap();
        EntityUid canvas = default, frame = default;
        await server.WaitPost(() =>
        {
            canvas = entities.SpawnEntity("DLArtCanvas", map.GridCoords);
            frame = entities.SpawnEntity("DLArtFrame", map.GridCoords);
            var data = entities.GetComponent<ArtCanvasComponent>(canvas);
            data.WorkId = row.WorkId; data.RevisionHash = row.RevisionHash;
            data.Document = row.Document; data.SavedPreview = row.Preview;
            entities.GetComponent<ArtDisplayComponent>(canvas).Preview = row.Preview;
            entities.GetComponent<ArtDisplayComponent>(frame).Preview = row.Preview;
            entities.System<SharedContainerSystem>().Insert(canvas,
                entities.System<SharedContainerSystem>().EnsureContainer<ContainerSlot>(frame, "art-canvas"));
        });
        await Send(new ArtModerationRequest("delete", row.Id));
        Assert.That(await db.GetArtworkAsync(row.Id), Is.Null);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ArtCanvasComponent>(canvas).Document, Is.EqualTo(ArtLimits.EmptyDocument));
            Assert.That(entities.GetComponent<ArtDisplayComponent>(canvas).Preview, Is.Empty);
            Assert.That(entities.GetComponent<ArtDisplayComponent>(frame).Preview, Is.Empty);
        });
        Assert.That(await db.ArchiveArtworkAsync(row), Is.EqualTo(row.Id));
        Assert.That(await db.GetArtworkPoolAsync(), Is.Empty);
        var sentinel = new ArchivedArtwork { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), RevisionHash = baked.Hash,
            Document = baked.Json, Preview = baked.Preview, Title = baked.Title, CreatedAt = DateTime.UtcNow };
        await db.ArchiveArtworkAsync(sentinel);
        await Send(new ArtModerationRequest("view", sentinel.Id));
        Assert.That(((ArtModerationEuiState) ui.GetNewState()).Selected!.Id, Is.EqualTo(sentinel.Id));
        await server.WaitPost(() => admins.DeAdmin(pair.Player!));
        Assert.That(ui.IsShutDown, Is.True, "Revoking active admin status must close the menu");
        await Send(new ArtModerationRequest("view", sentinel.Id));
        await Send(new ArtModerationRequest("delete", sentinel.Id));
        Assert.That(((ArtModerationEuiState) ui.GetNewState()).Image, Is.Empty);
        Assert.That(await db.GetArtworkAsync(sentinel.Id), Is.Not.Null);
        await pair.CleanReturnAsync();
    }
}
