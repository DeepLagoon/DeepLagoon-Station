#nullable enable
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Shared.Administration.Notes;
using Content.Shared.Database;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ModerationWebhookTests
{
    private sealed class WebhookHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Requests = new();
        public int Successes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Requests.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"retry_after\":1}") };
            Interlocked.Increment(ref Successes);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"123\"}") };
        }
    }

    [Test]
    public async Task PersistentOutboxReplaysAndRetriesRateLimitedDelivery()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var system = server.ResolveDependency<IEntityManager>().System<ModerationWebhookSystem>();
        var config = server.ResolveDependency<IConfigurationManager>();
        var database = server.ResolveDependency<IServerDbManager>();
        var directory = Path.Combine(Path.GetTempPath(), "moderation-outbox-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var handler = new WebhookHandler();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var http = typeof(ModerationWebhookSystem).GetField("_http", flags)!;
        var outbox = typeof(ModerationWebhookSystem).GetField("_outbox", flags)!;
        var restoredPlayer = new NetUserId(Guid.NewGuid());
        var restoredBan = 0;
        await PoolManager.WaitUntil(server, async () =>
        {
            await database.UpdatePlayerRecordAsync(restoredPlayer, "RestoredPlayer", IPAddress.Loopback, null);
            restoredBan = await database.AddServerBanAsync(new ServerBanDef(null, restoredPlayer, null, null,
                DateTimeOffset.UtcNow, null, null, TimeSpan.Zero, "restored event", NoteSeverity.High, restoredPlayer, null));
            return true;
        });
        var change = new ModerationEvent(NoteType.ServerBan, restoredBan, "Снят", restoredPlayer, null, "restored event",
            NoteSeverity.High, false, null, null, null, DateTimeOffset.UtcNow);
        var pending = new ModerationWebhookSystem.Pending(change, "Main", ModerationWebhookFormatter.Format(change, "Main", "Player", "Admin"));
        var saved = Path.Combine(directory, "0000000000000000000-restored.json");
        var hidden = Path.Combine(directory, "0000000000000000000-hidden.json");
        var deleted = Path.Combine(directory, "0000000000000000000-deleted.json");
        // Old versions may have already cached payloads for notes that are now excluded.
        await File.WriteAllTextAsync(hidden, JsonSerializer.Serialize(pending with
        {
            Change = change with { Type = NoteType.Note, Secret = true }
        }));
        await File.WriteAllTextAsync(deleted, JsonSerializer.Serialize(pending with
        {
            Change = change with { Type = NoteType.Note, Action = "Удалено" }
        }));
        await File.WriteAllTextAsync(saved, JsonSerializer.Serialize(pending));
        try
        {
            await server.WaitPost(() =>
            {
                ((HttpClient) http.GetValue(system)!).Dispose();
                http.SetValue(system, new HttpClient(handler));
                outbox.SetValue(system, directory);
                config.SetCVar(CCVars.DiscordModerationWebhook, "https://discord.com/api/webhooks/123/" + new string('a', 30));
            });
            await PoolManager.WaitUntil(server, async () =>
            {
                await Task.Delay(100);
                return handler.Successes == 1 && !File.Exists(saved) && !File.Exists(hidden) && !File.Exists(deleted);
            }, maxTicks: 600);
            Assert.That(handler.Requests, Has.Count.EqualTo(2), "429 must retry the saved event.");
            var requests = handler.Requests.ToArray();
            Assert.That(requests[0], Is.EqualTo(requests[1]));
            using var payload = JsonDocument.Parse(requests[1]);
            Assert.That(payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength(), Is.Zero);
            var uid = new NetUserId(Guid.NewGuid());
            await PoolManager.WaitUntil(server, async () =>
            {
                await database.UpdatePlayerRecordAsync(uid, "WebhookPlayer", IPAddress.Loopback, null);
                await database.AddAdminNote(null, uid.UserId, TimeSpan.Zero, "hidden note", NoteSeverity.High,
                    true, uid.UserId, DateTimeOffset.UtcNow, null);
                Assert.That(Directory.EnumerateFiles(directory, "*.json"), Is.Empty,
                    "Hidden notes must not be queued for Discord.");
                await database.AddAdminNote(null, uid.UserId, TimeSpan.Zero, "live note @everyone", NoteSeverity.High,
                    false, uid.UserId, DateTimeOffset.UtcNow, null);
                return true;
            });
            await PoolManager.WaitUntil(server, async () =>
            {
                await Task.Delay(100);
                return handler.Successes == 2 && !Directory.EnumerateFiles(directory, "*.json").Any();
            }, maxTicks: 600);
            Assert.That(handler.Requests.Last(), Does.Contain("WebhookPlayer"));
        }
        finally
        {
            await server.WaitPost(() => config.SetCVar(CCVars.DiscordModerationWebhook, ""));
            Directory.Delete(directory, true);
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AllCommittedModerationTypesProduceEvents()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var database = pair.Server.ResolveDependency<IServerDbManager>();
        var player = new NetUserId(Guid.NewGuid());
        var actor = new NetUserId(Guid.NewGuid());
        var events = new List<ModerationEvent>();
        void Observe(ModerationEvent change) => events.Add(change);
        database.ModerationChanged += Observe;
        try
        {
            await PoolManager.WaitUntil(pair.Server, async () =>
            {
                await database.UpdatePlayerRecordAsync(player, "WebhookPlayer", IPAddress.Loopback, null);
                await database.UpdatePlayerRecordAsync(actor, "WebhookActor", IPAddress.Loopback, null);
                var now = DateTimeOffset.UtcNow;
                var expires = now.AddHours(1);
                var banId = await database.AddServerBanAsync(new ServerBanDef(null, player, null, null, now,
                    expires, null, TimeSpan.Zero, "server reason", NoteSeverity.High, actor, null));
                await database.EditServerBan(banId, "edited ban", NoteSeverity.Minor, expires, actor.UserId, now);
                await database.AddServerUnbanAsync(new ServerUnbanDef(banId, actor, now));
                await database.HideServerBanFromNotes(banId, actor.UserId, now);
                var role = await database.AddServerRoleBanAsync(new ServerRoleBanDef(null, player, null, null, now,
                    expires, null, TimeSpan.Zero, "role reason", NoteSeverity.Medium, actor, null, "Job:Contractor"));
                await database.EditServerRoleBan(role.Id!.Value, "edited role", NoteSeverity.High, expires, actor.UserId, now);
                await database.AddServerRoleUnbanAsync(new ServerRoleUnbanDef(role.Id.Value, actor, now));
                await database.HideServerRoleBanFromNotes(role.Id.Value, actor.UserId, now);
                var note = await database.AddAdminNote(null, player.UserId, TimeSpan.Zero, "secret note", NoteSeverity.High, true, actor.UserId, now, expires);
                await database.EditAdminNote(note, "edited note", NoteSeverity.Minor, false, actor.UserId, now, null);
                await database.DeleteAdminNote(note, actor.UserId, now);
                var watch = await database.AddAdminWatchlist(null, player.UserId, TimeSpan.Zero, "watch", actor.UserId, now, expires);
                await database.EditAdminWatchlist(watch, "edited watch", actor.UserId, now, expires);
                await database.DeleteAdminWatchlist(watch, actor.UserId, now);
                var message = await database.AddAdminMessage(null, player.UserId, TimeSpan.Zero, "message", actor.UserId, now, null);
                await database.EditAdminMessage(message, "edited message", actor.UserId, now, expires);
                await database.DeleteAdminMessage(message, actor.UserId, now);
                return true;
            });
            Assert.That(events, Has.Count.EqualTo(17));
            Assert.That(events.All(e => e.Player == player && e.Actor == actor.UserId && e.Id > 0), Is.True);
            Assert.That(events.Where(e => e.Type == NoteType.RoleBan).All(e => e.Roles!.Contains("Job:Contractor")), Is.True);
            var edited = events.Single(e => e.Type == NoteType.Note && e.Action == "Изменено");
            Assert.That(edited.Message, Is.EqualTo("edited note"));
            Assert.That(edited.PreviousMessage, Is.EqualTo("secret note"));
            Assert.That(edited.PreviousSecret, Is.True);
            Assert.That(edited.Secret, Is.False);
            Assert.That(JsonSerializer.Serialize(ModerationWebhookFormatter.Format(edited, "Main", "Player", "Admin")),
                Does.Not.Contain("secret note"));
            Assert.That(edited.Expires, Is.Null);
            Assert.That(events.Single(e => e.Type == NoteType.Note && e.Action == "Удалено").Message, Is.EqualTo("edited note"));
            var count = events.Count;
            Assert.That(async () => await database.AddAdminNote(null, player.UserId,
                TimeSpan.Zero, "failed write", NoteSeverity.High, true, Guid.NewGuid(), DateTimeOffset.UtcNow, null), Throws.Exception);
            Assert.That(events, Has.Count.EqualTo(count), "Failed writes must not generate a notification.");
        }
        finally { database.ModerationChanged -= Observe; }
        await pair.CleanReturnAsync();
    }

    [Test]
    public void LongNotesPreserveTextDisableMentionsAndSurviveOutboxSerialization()
    {
        var text = "@everyone " + string.Concat(Enumerable.Repeat("длинная заметка 😀 ", 550));
        var change = new ModerationEvent(NoteType.Note, 42, "Изменено", new NetUserId(Guid.NewGuid()),
            Guid.NewGuid(), text, NoteSeverity.High, false, null, 123, null, DateTimeOffset.UtcNow, "old text", PreviousSecret: false);
        var payloads = ModerationWebhookFormatter.Format(change, "Main", "Player", "Admin");
        Assert.That(payloads, Has.Count.GreaterThan(1));
        Assert.That(string.Concat(payloads.Select(p => p.Embeds![0].Description)), Is.EqualTo("**Причина / текст**\n" + text + "\n\n**Предыдущий текст**\nold text"));
        foreach (var payload in payloads)
        {
            Assert.That(payload.AllowedMentions.Parse, Is.Empty);
            var embed = payload.Embeds![0];
            Assert.That(embed.Description.Length, Is.LessThanOrEqualTo(4096));
            Assert.That(embed.Description.Length + embed.Title.Length + embed.Footer!.Value.Text.Length +
                embed.Fields.Sum(f => f.Name.Length + f.Value.Length), Is.LessThanOrEqualTo(6000));
        }
        var saved = new ModerationWebhookSystem.Pending(change, "Main", payloads, 1);
        var restored = JsonSerializer.Deserialize<ModerationWebhookSystem.Pending>(JsonSerializer.Serialize(saved))!;
        Assert.That(restored.Change, Is.EqualTo(change));
        Assert.That(restored.Next, Is.EqualTo(1));
        Assert.That(restored.Payloads, Has.Count.EqualTo(payloads.Count));
    }

    [TestCase(NoteType.Note, true, "Создано", false)]
    [TestCase(NoteType.Note, true, "Изменено", false)]
    [TestCase(NoteType.Note, false, "Удалено", false)]
    [TestCase(NoteType.Note, true, "Удалено", false)]
    [TestCase(NoteType.Note, false, "Создано", true)]
    [TestCase(NoteType.Note, false, "Изменено", true)]
    [TestCase(NoteType.ServerBan, false, "Снят", true)]
    [TestCase(NoteType.RoleBan, false, "Снят", true)]
    [TestCase(NoteType.Watchlist, true, "Создано", false)]
    [TestCase(NoteType.Watchlist, false, "Изменено", false)]
    [TestCase(NoteType.Message, true, "Создано", false)]
    [TestCase(NoteType.ServerBan, false, "Скрыто из заметок", false)]
    [TestCase(NoteType.RoleBan, true, "Изменено", false)]
    public void NotificationPolicy(NoteType type, bool secret, string action, bool expected)
    {
        var change = new ModerationEvent(type, 42, action, null, null, "text",
            NoteSeverity.High, secret, null, null, null, DateTimeOffset.UtcNow);
        Assert.That(ModerationWebhookFormatter.ShouldNotify(change), Is.EqualTo(expected));
        Assert.That(ModerationWebhookFormatter.Format(change, "Main", "Player", "Admin").Count > 0,
            Is.EqualTo(expected));
    }

    [Test]
    public void HistoricalTextWithoutVisibilityProvenanceIsExcluded()
    {
        var change = new ModerationEvent(NoteType.Note, 42, "Изменено", null, null, "visible text",
            NoteSeverity.High, false, null, null, null, DateTimeOffset.UtcNow, "old private text");
        var legacy = JsonSerializer.Serialize(change).Replace(",\"PreviousSecret\":true", "");
        var restored = JsonSerializer.Deserialize<ModerationEvent>(legacy)!;
        var formatted = JsonSerializer.Serialize(ModerationWebhookFormatter.Format(restored, "Main", "Player", "Admin"));
        Assert.That(formatted, Does.Contain("visible text"));
        Assert.That(formatted, Does.Not.Contain("old private text"));
    }

    [Test]
    public async Task QueuedNotesUseCurrentVisibilityAndRegenerateLegacyEmbeds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var system = server.ResolveDependency<IEntityManager>().System<ModerationWebhookSystem>();
        var config = server.ResolveDependency<IConfigurationManager>();
        var database = server.ResolveDependency<IServerDbManager>();
        var directory = Path.Combine(Path.GetTempPath(), "moderation-privacy-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var handler = new WebhookHandler();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var player = new NetUserId(Guid.NewGuid());
        var visible = 0;
        var hidden = 0;
        var deleted = 0;
        await PoolManager.WaitUntil(server, async () =>
        {
            await database.UpdatePlayerRecordAsync(player, "PrivacyPlayer", IPAddress.Loopback, null);
            var now = DateTimeOffset.UtcNow;
            visible = await database.AddAdminNote(null, player.UserId, TimeSpan.Zero, "public revision",
                NoteSeverity.High, false, player.UserId, now, null);
            hidden = await database.AddAdminNote(null, player.UserId, TimeSpan.Zero, "now hidden",
                NoteSeverity.High, true, player.UserId, now, null);
            deleted = await database.AddAdminNote(null, player.UserId, TimeSpan.Zero, "now deleted",
                NoteSeverity.High, false, player.UserId, now, null);
            await database.DeleteAdminNote(deleted, player.UserId, now);
            return true;
        });
        foreach (var (id, text) in new[] { (visible, "public revision"), (hidden, "hidden leak"), (deleted, "deleted leak") })
        {
            var change = new ModerationEvent(NoteType.Note, id, "Изменено", player, player.UserId, text,
                NoteSeverity.High, false, null, null, null, DateTimeOffset.UtcNow, "legacy private history", PreviousSecret: false);
            // Deliberately cache old embeds which would expose the previous hidden revision.
            var pending = new ModerationWebhookSystem.Pending(change, "Main", ModerationWebhookFormatter.Format(change, "Main", "Player", "Admin"));
            await File.WriteAllTextAsync(Path.Combine(directory, $"{id:D19}.json"), JsonSerializer.Serialize(pending));
        }
        try
        {
            await server.WaitPost(() =>
            {
                var http = typeof(ModerationWebhookSystem).GetField("_http", flags)!;
                ((HttpClient) http.GetValue(system)!).Dispose();
                http.SetValue(system, new HttpClient(handler));
                typeof(ModerationWebhookSystem).GetField("_outbox", flags)!.SetValue(system, directory);
                config.SetCVar(CCVars.DiscordModerationWebhook, "https://discord.com/api/webhooks/123/" + new string('a', 30));
            });
            await PoolManager.WaitUntil(server, async () =>
            {
                await Task.Delay(100);
                return handler.Successes == 1 && !Directory.EnumerateFiles(directory, "*.json").Any();
            }, maxTicks: 600);
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            foreach (var payload in handler.Requests)
            {
                Assert.That(payload, Does.Contain("public revision"));
                Assert.That(payload, Does.Not.Contain("legacy private history"));
                Assert.That(payload, Does.Not.Contain("hidden leak"));
                Assert.That(payload, Does.Not.Contain("deleted leak"));
            }
        }
        finally
        {
            await server.WaitPost(() => config.SetCVar(CCVars.DiscordModerationWebhook, ""));
            Directory.Delete(directory, true);
        }
        await pair.CleanReturnAsync();
    }
}
