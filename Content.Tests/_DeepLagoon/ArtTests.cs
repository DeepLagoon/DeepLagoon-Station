using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Art;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Art;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace Content.Tests._DeepLagoon;

public static class ArtTestsFixturePng
{
    public static string Create()
    {
        using var image = new Image<Rgba32>(ArtLimits.Resolution, ArtLimits.Resolution, new Rgba32(48, 92, 128));
        using var stream = new MemoryStream(); image.Save(stream, new PngEncoder());
        return Convert.ToBase64String(stream.ToArray());
    }
}

[TestFixture]
public sealed class ArtTests
{
    private static string Png(Rgba32 color, int size = ArtLimits.Resolution)
    {
        using var image = new Image<Rgba32>(size, size, color);
        using var stream = new MemoryStream(); image.Save(stream, new PngEncoder());
        return Convert.ToBase64String(stream.ToArray());
    }
    private static string Project(params ArtDocument.Layer[] layers) => JsonSerializer.Serialize(new ArtDocument.Project
        { Title = "Тихая лагуна", Layers = layers.ToList() }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    [Test]
    public void MasksOpacityAndLayerOrderAreComposedByTheServerAndRemainEditable()
    {
        var json = Project(new ArtDocument.Layer { Name = "Красный", Png = Png(new Rgba32(255, 0, 0)) },
            new ArtDocument.Layer { Name = "Синий", Png = Png(new Rgba32(0, 0, 255)), Mask = Png(new Rgba32(128, 128, 128)), Opacity = .5f });
        var baked = ArtDocument.Bake(json);
        Assert.That(baked.HasInk, Is.True);
        using var image = Image.Load<Rgba32>(Convert.FromBase64String(baked.Preview));
        var pixel = image[64, 64];
        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(128));
            Assert.That(pixel.R, Is.InRange(188, 194)); Assert.That(pixel.B, Is.InRange(61, 67));
            Assert.That(pixel.A, Is.EqualTo(255));
            Assert.That(baked.Json, Does.Contain("mask")); Assert.That(JsonDocument.Parse(baked.Json).RootElement.GetProperty("layers")[0].GetProperty("name").GetString(), Is.EqualTo("Красный"));
            Assert.That(ArtDocument.Bake(baked.Json).Hash, Is.EqualTo(baked.Hash));
        });
    }
    [Test]
    public void EmptyOrFullyMaskedCanvasDoesNotBecomeAnArchiveEntry()
    {
        Assert.That(ArtDocument.Bake(ArtLimits.EmptyDocument).HasInk, Is.False);
        var hidden = Project(new ArtDocument.Layer { Name = "Скрытый", Png = Png(new Rgba32(255, 0, 0)), Mask = Png(new Rgba32(0, 0, 0)) });
        Assert.That(ArtDocument.Bake(hidden).HasInk, Is.False);
    }
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{\"version\":1,\"version\":2,\"layers\":[]}")]
    [TestCase("{\"version\":1,\"layers\":null}")]
    [TestCase("{\"version\":1,\"layers\":[null]}")]
    [TestCase("{\"version\":1,\"layers\":[{\"opacity\":-1}]}")]
    [TestCase("{\"version\":1,\"layers\":[{\"png\":\"not a PNG\"}]}")]
    public void MalformedProjectsAreRejected(string json) => Assert.That(() => ArtDocument.Bake(json), Throws.Exception);

    [Test]
    public void HiddenLayersAndWrongSizedPreviewsAreStillValidated()
    {
        Assert.That(() => ArtDocument.Bake(Project(new ArtDocument.Layer { Visible = false, Png = Png(new Rgba32(255, 0, 0), 1024) })), Throws.Exception);
        Assert.That(() => ArtDocument.NormalizePreview(Png(new Rgba32(255, 0, 0))), Throws.Exception);
        Assert.That(() => ArtDocument.Bake(new string(' ', ArtLimits.MaxDocumentChars + 1)), Throws.Exception);
        Assert.That(() => ArtDocument.Bake(Project(Enumerable.Range(0, 9).Select(_ => new ArtDocument.Layer()).ToArray())), Throws.Exception);
    }

    [Test]
    public async Task SqliteMigrationAndArchiveRoundTripPreserveLayersAndDeduplicateReframing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        await using (var context = new SqliteServerDbContext(options))
        {
            var before = context.Database.GetMigrations().TakeWhile(m => !m.EndsWith("_ArtArchive")).Last();
            await context.GetService<IMigrator>().MigrateAsync(before);
            context.Preference.Add(new Preference { UserId = Guid.NewGuid(), AdminOOCColor = "#FFFFFF" }); await context.SaveChangesAsync();
            await context.Database.MigrateAsync();
            Assert.That(context.Database.HasPendingModelChanges(), Is.False);
            Assert.That(await context.Preference.CountAsync(), Is.EqualTo(1));
        }
        var db = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var workId = Guid.NewGuid(); var author = Guid.NewGuid();
        var baked = ArtDocument.Bake(Project(new ArtDocument.Layer { Name = "Чернила", Png = Png(new Rgba32(12, 22, 33)) }));
        ArchivedArtwork Row() => new() { Id = Guid.NewGuid(), WorkId = workId, RevisionHash = baked.Hash, Document = baked.Json,
            Preview = baked.Preview, Title = baked.Title, Author = author, AuthorName = "Художник", CreatedAt = DateTime.UtcNow };
        var id = await db.ArchiveArtworkAsync(Row());
        Assert.That(await db.ArchiveArtworkAsync(Row()), Is.EqualTo(id));
        Assert.That(await db.GetArtworkPoolAsync(), Is.EqualTo(new[] { id }));
        var loaded = await db.GetArtworkAsync(id);
        Assert.Multiple(() => { Assert.That(loaded.Document, Is.EqualTo(baked.Json)); Assert.That(loaded.Preview, Is.EqualTo(baked.Preview)); Assert.That(loaded.Author, Is.EqualTo(author)); });
        var listed = await db.ListArtworksAsync("хУдОж", 0);
        Assert.That(listed.Single().Id, Is.EqualTo(id));
        Assert.That(listed.Single().Document, Is.Empty, "Library pages must not load editable layers");
        Assert.That((await db.ListArtworksAsync(author.ToString(), 0)).Single().Id, Is.EqualTo(id));
        Assert.That(await db.ListArtworksAsync("no matching artwork", 0), Is.Empty);
        Assert.That(await db.DeleteArtworkAsync(id), Is.True);
        Assert.That(await db.DeleteArtworkAsync(id), Is.False);
        Assert.That(await db.ArchiveArtworkAsync(Row()), Is.EqualTo(id), "Removed revisions must remain tombstoned");
        await using var read = new SqliteServerDbContext(options);
        Assert.That(await read.ArchivedArtworks.CountAsync(), Is.EqualTo(1));
        var removed = await read.ArchivedArtworks.SingleAsync();
        Assert.That(removed.Document, Is.Empty);
        Assert.That(removed.Preview, Is.Empty);
        Assert.That(removed.Hidden, Is.True);
        Assert.That(await db.ListArtworksAsync("", 0), Is.Empty);
        Assert.That(await db.GetArtworkPoolAsync(), Is.Empty);
        Assert.That(await db.GetArtworkAsync(id), Is.Null);
        read.ArchivedArtworks.AddRange(Enumerable.Range(0, 22).Select(i => new ArchivedArtwork
        {
            Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), RevisionHash = baked.Hash,
            Title = "Страница " + i, AuthorName = "Неизвестный", CreatedAt = DateTime.UtcNow.AddMinutes(i),
        }));
        await read.SaveChangesAsync();
        var first = await db.ListArtworksAsync("", 0);
        var second = await db.ListArtworksAsync("", 1);
        Assert.That(first, Has.Count.EqualTo(ArtModerationEuiState.PageSize + 1));
        Assert.That(second, Has.Count.EqualTo(2));
        Assert.That(first.Take(ArtModerationEuiState.PageSize).Select(a => a.Id).Intersect(second.Select(a => a.Id)), Is.Empty);
        Assert.That(await db.ListArtworksAsync("no matching artwork", 0), Is.Empty, "Unknown authors must not match arbitrary searches");
        Assert.That((await db.ListArtworksAsync("стРАНИца", 1)), Has.Count.EqualTo(2));
    }
    [Test]
    public void ModerationImagePreservesFullResolutionAndRevisionHash()
    {
        var json = Project(new ArtDocument.Layer { Png = Png(new Rgba32(90, 110, 130)) });
        var normal = ArtDocument.Bake(json);
        var full = ArtDocument.Bake(json, fullResolution: true);
        using var image = Image.Load<Rgba32>(Convert.FromBase64String(full.Preview));
        Assert.That(image.Width, Is.EqualTo(ArtLimits.Resolution));
        Assert.That(image.Height, Is.EqualTo(ArtLimits.Resolution));
        Assert.That(full.Hash, Is.EqualTo(normal.Hash));
        Assert.That(full.Json, Is.EqualTo(normal.Json));
    }
    [Test]
    public void PostgresMigrationMatchesModelAndAddsOnlyTheArchive()
    {
        using var context = new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var id = context.Database.GetMigrations().Single(m => m.EndsWith("_ArtArchive"));
        var before = context.Database.GetMigrations().TakeWhile(m => m != id).Last();
        var sql = context.GetService<IMigrator>().GenerateScript(before, id);
        Assert.Multiple(() => { Assert.That(sql, Does.Contain("CREATE TABLE archived_artworks")); Assert.That(sql, Does.Contain("timestamp with time zone")); Assert.That(sql, Does.Contain("CREATE UNIQUE INDEX")); Assert.That(sql, Does.Not.Contain("DROP TABLE")); Assert.That(sql, Does.Not.Contain("ALTER TABLE preference")); });
    }
}
