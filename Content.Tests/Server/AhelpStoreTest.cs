using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.Ahelp;
using Content.Server._DeepLagoon.Ahelp;
using Content.Shared._DeepLagoon.Ahelp;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Tests.Server;

[TestFixture]
public sealed class AhelpStoreTest
{
    [Test]
    public void NewRoundDeletesTicketsHistoryAttachmentsAndBlockedRelayWork()
    {
        using var store = new AhelpStore(":memory:");
        var player = Guid.NewGuid();
        var admin = Guid.NewGuid();
        store.ResetRound("old-round");
        var media = store.Send(default, player, "Player", player, "Player", false, false, "old caption", "old-media", 1,
            new AhelpStore.Attachment("screen.png", AhelpLimits.Loading, [])).Event!;
        store.QueueMedia(media.Id);
        store.AckMedia(media.Id, false, 1000);
        store.Change(media.Ticket.Id, admin, "Admin", AhelpAction.Claim, "", "claim", 2);
        store.Reserve(media.Ticket.Id);
        store.Change(media.Ticket.Id, admin, "Admin", AhelpAction.Close, "", "close", 3);

        store.ResetRound("new-round");
        Assert.That(store.Get(media.Ticket.Id), Is.Null);
        Assert.That(store.GetEvent("old-media"), Is.Null);
        Assert.That(store.History(media.Ticket.Number, 0), Is.Empty);
        Assert.That(store.Read(player, false, media.Ticket.Id, player, 0).Tickets, Is.Empty);
        Assert.That(store.Read(admin, true, media.Ticket.Id, player, 0).Selected, Is.Null);
        Assert.That(store.ReadAttachment(media.Id, admin, true, true), Is.Null);
        Assert.That(store.Pending(10000), Is.Empty);
        Assert.That(store.PendingMedia(10000), Is.Empty);
        Assert.That(store.Results(player), Is.Empty);
        Assert.That(store.CompleteAttachment("old-media", new AhelpStore.Attachment("screen.png", "image/png", [1])).Error, Is.EqualTo("missing"));
        Assert.That(store.Send(default, player, "Player", player, "Player", false, false, "late old message", "old-send", 4,
            currentRound: () => false).Error, Is.EqualTo("closed"));
        var batch = store.RelayBatch(false, 10000);
        Assert.That(batch.Round, Is.EqualTo("new-round"));
        Assert.That(batch.Tickets, Is.Empty);
        var fresh = store.Send(default, player, "Player", player, "Player", false, false, "new message", "new-send", 5).Event!;
        Assert.That(fresh.Ticket.Id, Is.Not.EqualTo(media.Ticket.Id));
        Assert.That(fresh.Id, Is.GreaterThan(media.Id));
        Assert.That(fresh.Ticket.Number, Is.GreaterThan(media.Ticket.Number));
    }

    [Test]
    public void StartupClearsOldConversationsButPreservesEarnedRewardsExactlyOnce()
    {
        var path = Path.GetTempFileName();
        var player = Guid.NewGuid();
        var admin = Guid.NewGuid();
        Guid id;
        long reward;
        try
        {
            using (var store = new AhelpStore(path))
            {
                id = store.Send(default, player, "Player", admin, "Admin", true, false, "help", "create", 1).Event!.Ticket.Id;
                reward = store.Change(id, admin, "Admin", AhelpAction.Resolve, "", "resolve", 2).Event!.Id;
            }
            using (var restarted = AhelpStore.OpenForServer(path, 3, "next-round"))
            {
                Assert.That(restarted.Get(id), Is.Null);
                Assert.That(restarted.Pending(3), Is.Empty);
                Assert.That(restarted.RelayBatch(false, 3).Round, Is.EqualTo("next-round"));
                var earned = restarted.Rewards().Single();
                Assert.That(earned.Id, Is.EqualTo(reward));
                Assert.That(earned.Ticket.Id, Is.EqualTo(id));
                Assert.That(earned.Line.Author, Is.EqualTo(admin));
                restarted.ResetRound("another-round");
                Assert.That(restarted.Rewards().Length, Is.EqualTo(1));
                restarted.MarkReward(reward);
            }
            using var final = AhelpStore.OpenForServer(path, 4);
            Assert.That(final.Rewards(), Is.Empty);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void RoundCleanupPreservesResolvedRewardWhileDiscordClosureIsStillWaiting()
    {
        using var store = new AhelpStore(":memory:");
        var player = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var ticket = store.Send(default, player, "Player", admin, "Admin", true, false, "help", "create", 1).Event!.Ticket.Id;
        store.Reserve(ticket);
        store.Change(ticket, admin, "Admin", AhelpAction.Resolve, "", "resolve", 2);
        Assert.That(store.Get(ticket)!.Status, Is.EqualTo(AhelpStatus.Closing));
        store.ResetRound("next");
        var reward = store.Rewards().Single();
        Assert.That(reward.Ticket.Id, Is.EqualTo(ticket));
        Assert.That(reward.Line.Author, Is.EqualTo(admin));
        store.MarkReward(reward.Id);
        Assert.That(store.Rewards(), Is.Empty);
    }

    [Test]
    public async Task ConcurrentClaimAndFinishRemainUniqueAcrossRestart()
    {
        var path = Path.GetTempFileName();
        var player = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Guid id;
        try
        {
            using (var store = new AhelpStore(path))
            {
                var created = store.Send(default, player, "Player", player, "Player", false, false, "first", "create", 1);
                id = created.Event!.Ticket.Id;
                Assert.That(store.Send(default, player, "Player", player, "Player", false, false, "first", "create", 1).Applied, Is.False);
                Assert.That(store.ReminderAssignees(1), Is.Empty);
                var results = await Task.WhenAll(
                    Task.Run(() => store.Change(id, first, "First", AhelpAction.Claim, "", "game-click", 2)),
                    Task.Run(() => store.Change(id, second, "Second", AhelpAction.Claim, "", "discord-click", 2)));
                Assert.That(results.Count(result => result.Applied), Is.EqualTo(1));
                Assert.That(results.Single(result => !result.Applied).Error, Is.EqualTo("already_claimed"));
                Assert.That(store.ReminderAssignees(2), Is.Null);
                var assignee = store.Get(id)!.Assignee!.Value;
                Assert.That(store.Reserve(id), Is.True);
                Assert.That(store.Change(id, assignee, "Staff", AhelpAction.Resolve, "", "resolve", 3).Event!.Kind, Is.EqualTo("closing"));
                Assert.That(store.Rewards(), Is.Empty);
                Assert.That(store.Send(id, player, "", player, "Player", false, false, "last Discord message", "drain", 4, drain: true).Applied, Is.True);
            }

            using var restarted = new AhelpStore(path);
            var history = restarted.Read(first, true, id, default, 0);
            Assert.That(history.Lines.Select(line => line.Text), Is.EqualTo(new[] { "first", "last Discord message" }));
            Assert.That(restarted.Complete(id, 5).Applied, Is.True);
            Assert.That(restarted.Complete(id, 5).Applied, Is.False);
            var reward = restarted.Rewards().Single();
            Assert.That(reward.Line.Author, Is.EqualTo(restarted.Get(id)!.Assignee));
            restarted.MarkReward(reward.Id);
            Assert.That(restarted.Rewards(), Is.Empty);
            Assert.That(restarted.Read(player, false, id, default, 0).Lines, Is.Empty);
            Assert.That(restarted.Results(player).Single().Ticket.Status, Is.EqualTo(AhelpStatus.Resolved));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void ReminderExcludesAssigneesUntilTheirTicketsAreResolvedOrReleased()
    {
        using var store = new AhelpStore(":memory:");
        var busy = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var firstPlayer = Guid.NewGuid();
        var secondPlayer = Guid.NewGuid();
        var first = store.Send(default, firstPlayer, "First", firstPlayer, "First", false, false, "help", "first", 1).Event!.Ticket.Id;
        var second = store.Send(default, secondPlayer, "Second", secondPlayer, "Second", false, false, "help", "second", 1).Event!.Ticket.Id;
        store.Change(first, busy, "Admin", AhelpAction.Claim, "", "game:claim", 2);
        store.Change(second, busy, "Admin", AhelpAction.Claim, "", "discord:claim", 2);
        store.Send(default, queued, "Queued", queued, "Queued", false, false, "help", "queued", 3);

        Assert.That(store.ReminderAssignees(2), Is.Null);
        Assert.That(store.ReminderAssignees(3), Is.EquivalentTo(new[] { busy }));
        Assert.That(store.Reserve(first), Is.True);
        store.Change(first, busy, "Admin", AhelpAction.Resolve, "", "resolve", 4);
        Assert.That(store.ReminderAssignees(4), Is.EquivalentTo(new[] { busy }));
        store.Release(second, busy, 5);
        Assert.That(store.ReminderAssignees(5), Is.Empty);
    }

    [Test]
    public void PrivateNotesAndAttachmentsAreVisibleOnlyToStaff()
    {
        using var store = new AhelpStore(":memory:");
        var player = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var id = store.Send(default, player, "Player", player, "Player", false, false, "help", "new", 1).Event!.Ticket.Id;
        var file = new AhelpStore.Attachment("test.gif", "image/gif", [4, 5]);
        var staff = store.Send(id, player, "", admin, "Admin", true, true, "staff", "note", 2, file).Event!;
        Assert.That(store.Read(player, false, id, default, 0).Lines.Length, Is.EqualTo(1));
        Assert.That(store.Read(unrelated, false, id, default, 0).Lines, Is.Empty);
        Assert.That(store.ReadAttachment(staff.Id, player, false, false), Is.Null);
        Assert.That(store.ReadAttachment(staff.Id, unrelated, false, false), Is.Null);
        Assert.That(store.ReadAttachment(staff.Id, admin, true, false)!.Bytes, Is.EqualTo(file.Bytes));
        Assert.That(store.Pending(2).Single().Kind, Is.EqualTo("created"));
        store.Ack(store.Pending(2).Single().Id, true, 0);
        Assert.That(store.Pending(2).Single().Kind, Is.EqualTo("claimed"));
        store.Ack(store.Pending(2).Single().Id, true, 0);
        var next = store.Pending(2).Single().Id;
        store.Ack(next, false, 10);
        Assert.That(store.Pending(9), Is.Empty);
        Assert.That(store.Pending(10).Single().Id, Is.EqualTo(next));
    }

    [Test]
    public async Task FirstAdminReplyClaimsAtomicallyAndKeepsOtherAdminsPrivate()
    {
        using var store = new AhelpStore(":memory:");
        var player = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var id = store.Send(default, player, "Player", player, "Player", false, false, "help", "create", 1).Event!.Ticket.Id;
        var replies = await Task.WhenAll(
            Task.Run(() => store.Send(id, player, "Player", first, "First", true, false, "reply", "reply:first", 2)),
            Task.Run(() => store.Send(id, player, "Player", second, "Second", true, false, "reply", "reply:second", 2)));
        Assert.That(replies.Count(reply => reply.Applied), Is.EqualTo(1));
        Assert.That(replies.Single(reply => !reply.Applied).Error, Is.EqualTo("assigned_to_other"));
        Assert.That(store.Get(id)!.Status, Is.EqualTo(AhelpStatus.Claimed));
        Assert.That(store.History(1, 0).Count(entry => entry.Kind == "claimed"), Is.EqualTo(1));
        var other = store.Get(id)!.Assignee == first ? second : first;
        Assert.That(store.Send(id, player, "Player", other, "Other", true, true, "note", "note", 3).Applied, Is.True);
        Assert.That(store.Read(player, false, id, player, 0).Lines.Select(line => line.Text), Is.EqualTo(new[] { "help", "reply" }));
    }

    [Test]
    public void LoadingAttachmentRetainsPositionAndDoesNotHoldText()
    {
        using var store = new AhelpStore(":memory:");
        var player = Guid.NewGuid();
        var loading = new AhelpStore.Attachment("screen.png", AhelpLimits.Loading, []);
        var media = store.Send(default, player, "Player", player, "Player", false, false, "caption", "upload", 1, loading).Event!;
        var next = store.Send(media.Ticket.Id, player, "Player", player, "Player", false, false, "next", "text", 2).Event!;
        Assert.That(store.QueueMedia(media.Id), Is.True);
        Assert.That(store.PendingMedia(2), Is.Empty);
        Assert.That(store.Pending(2).Single().Id, Is.EqualTo(next.Id));
        var ready = store.CompleteAttachment("upload", new AhelpStore.Attachment("screen.png", "image/png", [1, 2, 3]));
        Assert.That(ready.Event!.Id, Is.EqualTo(media.Id));
        Assert.That(store.PendingMedia(2).Single().Line.Mime, Is.EqualTo("image/png"));
        Assert.That(store.Read(player, false, media.Ticket.Id, player, 0).Lines.Select(line => line.Text), Is.EqualTo(new[] { "caption", "next" }));
        Assert.That(store.CompleteAttachment("upload", null).Applied, Is.False);
        Assert.That(store.ReadAttachment(media.Id, player, false, false)!.Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void AbandonedMediaBecomesFailedAndCanLeaveRelayQueue()
    {
        using var store = new AhelpStore(":memory:");
        var player = Guid.NewGuid();
        var media = store.Send(default, player, "Player", player, "Player", false, false, "", "upload", 1,
            new AhelpStore.Attachment("screen.png", AhelpLimits.Loading, [])).Event!;
        store.QueueMedia(media.Id);
        Assert.That(store.ExpireAttachments(0), Is.Empty);
        Assert.That(store.ExpireAttachments(1).Single().Event!.Line.Mime, Is.EqualTo(AhelpLimits.Failed));
        Assert.That(store.PendingMedia(2).Single().Id, Is.EqualTo(media.Id));
        Assert.That(store.ReadAttachment(media.Id, player, false, false), Is.Null);
        store.AckMedia(media.Id, true, 0);
        Assert.That(store.PendingMedia(2), Is.Empty);
        var retry = store.CompleteAttachment("upload", new AhelpStore.Attachment("screen.png", "image/png", [1, 2, 3]));
        Assert.That(retry.Event!.Id, Is.EqualTo(media.Id));
        Assert.That(store.PendingMedia(2).Single().Line.Mime, Is.EqualTo("image/png"));
    }

    [Test]
    public void MediaRetryDoesNotHoldTextAndClosureWaitsForMedia()
    {
        var path = Path.GetTempFileName();
        try
        {
            var player = Guid.NewGuid();
            var admin = Guid.NewGuid();
            var file = new AhelpStore.Attachment("banner.png", "image/png", [1, 2, 3]);
            long media;
            long closing;
            using (var store = new AhelpStore(path))
            {
                var first = store.Send(default, player, "Player", player, "Player", false, false, "", "image", 1, file).Event!;
                var ticket = first.Ticket.Id;
                media = first.Id;
                var followup = store.Send(ticket, player, "", player, "Player", false, false, "text", "text", 2).Event!;
                Assert.That(store.Pending(2).Single().Id, Is.EqualTo(media));
                Assert.That(store.QueueMedia(media), Is.True);
                Assert.That(store.QueueMedia(media), Is.False);
                store.AckMedia(media, false, 10);
                Assert.That(store.PendingMedia(2), Is.Empty);
                Assert.That(store.Pending(2).Single().Id, Is.EqualTo(followup.Id));
                store.Ack(followup.Id, true, 0);
                var claimed = store.Change(ticket, admin, "Admin", AhelpAction.Claim, "", "claim", 3).Event!;
                store.Ack(claimed.Id, true, 0);
                Assert.That(store.Reserve(ticket), Is.True);
                closing = store.Change(ticket, admin, "Admin", AhelpAction.Resolve, "", "close", 4).Event!.Id;
                Assert.That(store.Pending(4), Is.Empty);
            }
            using var restarted = new AhelpStore(path);
            Assert.That(restarted.PendingMedia(10).Single().Id, Is.EqualTo(media));
            Assert.That(restarted.ReadAttachment(media, player, false, false)!.Bytes, Is.EqualTo(file.Bytes));
            restarted.AckMedia(media, true, 0);
            Assert.That(restarted.Pending(10).Single().Id, Is.EqualTo(closing));
        }
        finally { File.Delete(path); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DiscordPreviewBoundsPayloadAndPreservesGifAnimation(bool animated)
    {
        using var image = new Image<Rgba32>(1280, 720);
        var random = new Random(42);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                foreach (ref var pixel in accessor.GetRowSpan(y))
                    pixel = new Rgba32((byte) random.Next(256), (byte) random.Next(256), (byte) random.Next(256));
            }
        });
        using var input = new MemoryStream();
        if (animated)
        {
            image.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 15;
            var second = image.Frames.AddFrame(image.Frames.RootFrame);
            second.Metadata.GetGifMetadata().FrameDelay = 25;
            second[0, 0] = new Rgba32(255, 255, 255);
            image.SaveAsGif(input);
        }
        else
            image.SaveAsPng(input);

        Assert.That(input.Length, Is.GreaterThan(1024 * 1024));
        var preview = AhelpImage.DiscordPreview(new AhelpStore.Attachment(
            animated ? "motion.gif" : "screenshot.png", animated ? "image/gif" : "image/png", input.ToArray()));
        Assert.That(preview, Is.Not.Null);
        Assert.That(preview!.Bytes.Length, Is.LessThanOrEqualTo(12 * 1024));
        Assert.That(preview.Mime, Is.EqualTo("image/webp"));
        using var result = Image.Load<Rgba32>(preview.Bytes);
        Assert.That(result.Width, Is.LessThanOrEqualTo(animated ? 256 : 448));
        Assert.That(result.Frames.Count, Is.EqualTo(animated ? 2 : 1));
        if (animated)
            Assert.That(result.Frames.Select(frame => frame.Metadata.GetWebpMetadata().FrameDelay),
                Is.EqualTo(new uint[] { 150, 250 }));
    }

    [Test]
    public void TicketPreviewKeepsGifFramesAndTimingWithinTheDownloadBudget()
    {
        using var source = new Image<Rgba32>(400, 200, new Rgba32(255, 0, 0));
        using var second = new Image<Rgba32>(400, 200, new Rgba32(0, 0, 255));
        source.Metadata.GetGifMetadata().RepeatCount = 0;
        source.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 15;
        source.Frames.AddFrame(second.Frames.RootFrame).Metadata.GetGifMetadata().FrameDelay = 25;
        using var stream = new MemoryStream();
        source.SaveAsGif(stream);

        var bytes = AhelpImage.Preview(AhelpImage.Read("preview.gif", stream.ToArray()));
        Assert.That(bytes, Is.Not.Null);
        Assert.That(bytes!.Length, Is.LessThanOrEqualTo(AhelpLimits.Preview));
        var preview = AhelpThumbnailData.Parse(bytes);
        Assert.That(preview, Is.Not.Null);
        Assert.That(preview.Height, Is.LessThanOrEqualTo(144));
        Assert.That(preview.Loops, Is.Zero);
        Assert.That(preview.FrameEnds, Is.EqualTo(new[] { 150, 400 }));
        using var atlas = Image.Load<Rgba32>(Convert.FromBase64String(preview.Image));
        Assert.That(atlas.Width, Is.EqualTo((preview.Width + 1) * 2));
        Assert.That(atlas[15, 15].R, Is.GreaterThan(200));
        Assert.That(atlas[preview.Width + 16, 15].B, Is.GreaterThan(200));
    }
}
