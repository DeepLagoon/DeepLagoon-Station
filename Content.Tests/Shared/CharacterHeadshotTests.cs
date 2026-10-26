using System.IO;
using System.Linq;
using Content.Server._DeepLagoon.CharacterInfo;
using Content.Shared.Preferences;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Tests.Shared;
[TestFixture]
public sealed class CharacterHeadshotTests
{
    [TestCase(0, false, 1, 1)]
    [TestCase(0, true, 2, 2)]
    [TestCase(1, false, 2, 2)]
    [TestCase(2, false, 3, 5)]
    [TestCase(3, false, 4, 10)]
    public void HeadshotSlotsFollowSupporterTier(int tier, bool booster, int active, int library)
    {
        Assert.That(Content.Shared._DeepLagoon.CharacterInfo.HeadshotLimits.ActiveSlots(tier, booster), Is.EqualTo(active));
        Assert.That(Content.Shared._DeepLagoon.CharacterInfo.HeadshotLimits.LibrarySlots(tier, booster), Is.EqualTo(library));
    }
    [TestCase(true)]
    [TestCase(false)]
    public void NotesAndHeadshotMigrationMatchesModel(bool postgres)
    {
        using DbContext context = postgres
            ? new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
                .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options)
            : new SqliteServerDbContext(new DbContextOptionsBuilder<SqliteServerDbContext>()
                .UseSqlite("Data Source=:memory:").Options);
        const string migration = "20261008160000_CharacterNotesAndHeadshot";
        var previous = context.Database.GetMigrations().TakeWhile(id => id != migration).Last();
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var sql = context.GetService<IMigrator>().GenerateScript(previous, migration);
        Assert.That(sql, Does.Contain("ooc_notes"));
        Assert.That(sql, Does.Contain("headshot_id"));
        Assert.That(sql, Does.Not.Contain("DROP TABLE"));
    }
    [Test]
    public void AcceptsStaticPngAndJpeg()
    {
        using var image=new Image<Rgba32>(32,32);
        using var png=new MemoryStream();image.SaveAsPng(png);
        Assert.That(HeadshotSystem.Validate(png.ToArray()),Is.EqualTo("image/png"));
        using var jpeg=new MemoryStream();image.SaveAsJpeg(jpeg);
        Assert.That(HeadshotSystem.Validate(jpeg.ToArray()),Is.EqualTo("image/jpeg"));
    }
    [Test]
    public void RejectsOversizeCorruptAndSvgFiles()
    {
        Assert.Throws<InvalidDataException>(()=>HeadshotSystem.Validate(new byte[1024*1024+1]));
        Assert.Throws<InvalidDataException>(()=>HeadshotSystem.Validate([]));
        Assert.Catch(()=>HeadshotSystem.Validate(System.Text.Encoding.UTF8.GetBytes("<svg onload='alert(1)'/>")));
        Assert.Catch(()=>HeadshotSystem.Validate([137,80,78,71,13,10,26,10]));
    }
    [Test]
    public void RejectsAnimationAndExcessiveDimensions()
    {
        using var image=new Image<Rgba32>(4097,1);using var png=new MemoryStream();image.SaveAsPng(png);
        Assert.Throws<InvalidDataException>(()=>HeadshotSystem.Validate(png.ToArray()));
        using var animated=new Image<Rgba32>(16,16);animated.Frames.AddFrame(animated.Frames.RootFrame);using var gif=new MemoryStream();animated.SaveAsGif(gif);
        Assert.Throws<InvalidDataException>(()=>HeadshotSystem.Validate(gif.ToArray()));
    }
    [Test]
    public void NotesAndHeadshotSurviveProfileCopies()
    {
        var profile=HumanoidCharacterProfile.DefaultWithSpecies("Human").WithOocNotes("**OOC** -=#ff8800(цвет)=-").WithHeadshotId("0123456789abcdef0123456789abcdef").WithFlavorText("__Описание__");
        var clone=profile.WithName("Another Name").WithAge(30);
        Assert.That(clone.OocNotes,Is.EqualTo(profile.OocNotes));Assert.That(clone.HeadshotId,Is.EqualTo(profile.HeadshotId));Assert.That(clone.FlavorText,Is.EqualTo(profile.FlavorText));
        Assert.That(profile.MemberwiseEquals(profile.WithOocNotes("other")),Is.False);
    }

    [Test]
    public void SponsorCanUploadAnimatedGifWithBoundedDecodedSize()
    {
        using var animated = new Image<Rgba32>(16, 16);
        animated.Frames.AddFrame(animated.Frames.RootFrame);
        using var gif = new MemoryStream(); animated.SaveAsGif(gif);
        Assert.Throws<InvalidDataException>(() => HeadshotSystem.Validate(gif.ToArray()));
        Assert.That(HeadshotSystem.Validate(gif.ToArray(), extended: true), Is.EqualTo("image/gif"));
        using var oversizedAnimation = new Image<Rgba32>(16, 16);
        for (var i = 0; i < 300; i++) oversizedAnimation.Frames.AddFrame(oversizedAnimation.Frames.RootFrame);
        using var hugeGif = new MemoryStream(); oversizedAnimation.SaveAsGif(hugeGif);
        Assert.Throws<InvalidDataException>(() => HeadshotSystem.Validate(hugeGif.ToArray(), extended: true));
    }

    [Test]
    public void SponsorGetsFiveMegabyteFileLimit()
    {
        using var image = new Image<Rgba32>(8, 8);
        using var png = new MemoryStream(); image.SaveAsPng(png);
        // Trailing bytes are legal for the decoder; exercise the independent file-size policy.
        var larger = new byte[1024 * 1024 + 1]; png.ToArray().CopyTo(larger, 0);
        Assert.Throws<InvalidDataException>(() => HeadshotSystem.Validate(larger));
        Assert.That(HeadshotSystem.Validate(larger, extended: true), Is.EqualTo("image/png"));
        Assert.Throws<InvalidDataException>(() => HeadshotSystem.Validate(new byte[5 * 1024 * 1024 + 1], extended: true));
    }

    [TestCase(false, 0, 0)]
    [TestCase(true, 0, 6)]
    [TestCase(false, 1, 6)]
    [TestCase(true, 1, 6)]
    [TestCase(false, 2, 9)]
    [TestCase(true, 2, 9)]
    [TestCase(false, 3, 12)]
    public void SupporterPointBonusFollowsHierarchy(bool booster, int tier, int expected)
        => Assert.That(Content.Shared._DeepLagoon.DiscordLink.SharedDiscordBoostSystem.LoadoutPointBonus(booster, tier), Is.EqualTo(expected));
}
