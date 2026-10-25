#nullable enable
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Shared.Administration;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using DbAdmin = Content.Server.Database.Admin;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DiscordModerationTests
{
    [Test]
    public async Task ModerationPersistsBansNotesAndRanksWithOfflineAuthor()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var system = server.ResolveDependency<IEntityManager>().System<DiscordLinkSystem>();
        var uid = new NetUserId(Guid.NewGuid());
        var actor = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());
        var path = Path.Combine(Path.GetTempPath(), "discord-moderation-" + Guid.NewGuid(), "links.db");
        using var store = new DiscordLinkStore(path);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var enabled = typeof(DiscordLinkSystem).GetField("_enabled", flags)!;
        var storeField = typeof(DiscordLinkSystem).GetField("_store", flags)!;
        var requestType = typeof(DiscordLinkSystem).GetNestedType("ModerationRequest", BindingFlags.NonPublic)!;
        var moderate = typeof(DiscordLinkSystem).GetMethod("Moderate", flags)!;
        try
        {
            await server.WaitPost(() => { enabled.SetValue(system, true); storeField.SetValue(system, store); });
            await PoolManager.WaitUntil(server, async () =>
            {
                foreach (var id in new[] { uid, actor, other })
                    await database.UpdatePlayerRecordAsync(id, "DiscordTest" + id, IPAddress.Loopback, null);
                await database.AddAdminRankAsync(new AdminRank { Name = "Game Admin", ShortName = "GA",
                    Flags = new[] { "ADMIN", "BAN", "DEBUG", "PERMISSIONS", "VIEWNOTES", "EDITNOTES" }
                        .Select(f => new AdminRankFlag { Flag = f }).ToList() });
                return true;
            });

            async Task<JsonElement> Call(string operation, object? extra = null, NetUserId? target = null)
            {
                var payload = JsonSerializer.SerializeToElement(extra ?? new { });
                var data = payload.EnumerateObject().ToDictionary(p => p.Name, p => (object) p.Value);
                data["Operation"] = operation;
                data["Uid"] = (target ?? uid).ToString();
                data["ActorUid"] = actor.ToString();
                var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(data), requestType)!;
                Task task = null!;
                await server.WaitPost(() => task = (Task) moderate.Invoke(system, new[] { request, (object) (target ?? uid), actor })!);
                await PoolManager.WaitUntil(server, () => task.IsCompleted);
                await task;
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                return JsonSerializer.SerializeToElement(result.GetType().GetProperty("Body")!.GetValue(result));
            }

            Assert.That((await Call("ban", new { Message = "test ban", Minutes = 15, Severity = "High" })).GetProperty("ok").GetBoolean(), Is.True);
            var ban = (await database.GetServerBansAsync(null, uid, null, null)).Single();
            Assert.That(ban.BanningAdmin, Is.EqualTo(actor));
            Assert.That(ban.ExpirationTime, Is.Not.Null);
            Assert.That((await Call("unban", new { Id = ban.Id }, other)).GetProperty("error").GetString(), Is.EqualTo("record_not_found"));
            await Call("unban", new { Id = ban.Id });
            Assert.That((await database.GetServerBanAsync(ban.Id!.Value))!.Unban!.UnbanningAdmin, Is.EqualTo(actor));

            await Call("job_ban", new { Message = "test job ban", Job = "Contractor", Minutes = 20 });
            var roleBan = (await database.GetServerRoleBansAsync(null, uid, null, null, true)).Single();
            Assert.That(roleBan.Role, Is.EqualTo("Job:Contractor"));
            await Call("job_unban", new { Id = roleBan.Id });
            Assert.That((await database.GetServerRoleBanAsync(roleBan.Id!.Value))!.Unban, Is.Not.Null);

            await Call("note_add", new { Message = "first note", Secret = true, Minutes = 30 });
            var note = (await database.GetAllAdminRemarks(uid.UserId)).OfType<AdminNoteRecord>().Single();
            Assert.That(note.CreatedBy!.UserId, Is.EqualTo(actor));
            Assert.That(note.Secret, Is.True);
            await Call("note_edit", new { Id = note.Id, Message = "edited note", Secret = false, Severity = "High" });
            Assert.That((await database.GetAdminNote(note.Id))!.Message, Is.EqualTo("edited note"));
            await Call("note_remove", new { Id = note.Id });
            Assert.That((await database.GetAdminNote(note.Id))!.Deleted, Is.True);

            await Call("staff_add", new { Kind = "trial" });
            var trial = await database.GetAdminDataForAsync(uid);
            Assert.That(trial, Is.Not.Null);
            var rank = await database.GetAdminRankAsync(trial!.AdminRankId!.Value);
            var actual = AdminFlagsHelper.NamesToFlags(rank!.Flags.Select(f => f.Flag));
            Assert.That(actual, Is.EqualTo(AdminFlags.Admin | AdminFlags.Ban | AdminFlags.ViewNotes | AdminFlags.EditNotes));
            Assert.That(store.StaffGrant(uid.UserId)!.Value.Kind, Is.EqualTo("trial"));
            await Call("staff_add", new { Kind = "admin" });
            Assert.That((await Call("staff_status")).GetProperty("kind").GetString(), Is.EqualTo("admin"));
            await Call("staff_remove", new { Kind = "admin" });
            Assert.That(await database.GetAdminDataForAsync(uid), Is.Null);
            Assert.That(store.StaffGrant(uid.UserId), Is.Null);
            await Call("staff_add", new { Kind = "mapper" });
            var mapper = await database.GetAdminDataForAsync(uid);
            var mapperRank = await database.GetAdminRankAsync(mapper!.AdminRankId!.Value);
            var mapperFlags = new[] { "ADMIN", "ADMINCHAT", "ADMINHELP", "ADMINWHO", "DEBUG", "MAPPING",
                "MODERATOR", "SERVER", "SPAWN", "STEALTH", "VIEWNOTES" };
            Assert.That(mapperRank!.Flags.Select(f => f.Flag), Is.EquivalentTo(mapperFlags));
            Assert.That(AdminFlagsHelper.NamesToFlags(mapperRank.Flags.Select(f => f.Flag)) &
                (AdminFlags.Ban | AdminFlags.MassBan), Is.EqualTo(AdminFlags.None));
            Assert.That((await Call("staff_remove", new { Kind = "admin" })).GetProperty("error").GetString(), Is.EqualTo("staff_conflict"));
            await Call("staff_remove", new { Kind = "mapper" });
            Assert.That(await database.GetAdminDataForAsync(uid), Is.Null);
            Assert.That(store.StaffGrant(uid.UserId), Is.Null);

            mapperRank.Flags = new() { new AdminRankFlag { Flag = "MAPPING" } };
            await database.UpdateAdminRankAsync(mapperRank);
            Assert.That((await Call("staff_add", new { Kind = "mapper" })).GetProperty("ok").GetBoolean(), Is.True);
            mapperRank = await database.GetAdminRankAsync(mapperRank.Id);
            Assert.That(mapperRank!.Flags.Select(f => f.Flag), Is.EquivalentTo(mapperFlags));
            Assert.That((await Call("staff_add", new { Kind = "mapper" })).GetProperty("ok").GetBoolean(), Is.True);
            Assert.That((await Call("staff_status")).GetProperty("kind").GetString(), Is.EqualTo("mapper"));

            mapperRank.Flags.Add(new AdminRankFlag { Flag = "BAN" });
            await database.UpdateAdminRankAsync(mapperRank);
            Assert.That((await Call("staff_add", new { Kind = "mapper" })).GetProperty("error").GetString(), Is.EqualTo("staff_rank_conflict"));
            Assert.That((await database.GetAdminRankAsync(mapperRank.Id))!.Flags.Select(f => f.Flag), Does.Contain("BAN"));
            await Call("staff_remove", new { Kind = "mapper" });

            await database.AddAdminAsync(new DbAdmin { UserId = uid.UserId, Flags = new() });
            Assert.That((await Call("staff_add", new { Kind = "admin" })).GetProperty("error").GetString(), Is.EqualTo("staff_conflict"));
            Assert.That(await database.GetAdminDataForAsync(uid), Is.Not.Null);

            Assert.That((await Call("staff_flags", new { Flag = "BAN", Mode = "grant" })).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
            await database.AddAdminAsync(new DbAdmin { UserId = actor.UserId,
                Flags = new() { new AdminFlag { Flag = "HOST" } } });
            Assert.That((await Call("staff_flags", new { Flag = "BAN", Mode = "grant" }, other)).GetProperty("error").GetString(), Is.EqualTo("admin_not_found"));
            foreach (var invalid in new[] { "NONE", "3", "BAN,HOST", "UNKNOWN" })
                Assert.That((await Call("staff_flags", new { Flag = invalid, Mode = "grant" })).GetProperty("error").GetString(), Is.EqualTo("invalid_request"));
            Assert.That((await Call("staff_flags", new { Flag = "BAN", Mode = "invalid" })).GetProperty("error").GetString(), Is.EqualTo("invalid_request"));
            var target = (await database.GetAdminDataForAsync(uid))!;
            target.AdminRankId = trial.AdminRankId;
            target.Title = "Keep title";
            target.Suspended = true;
            target.Flags.Add(new AdminFlag { Flag = "DEBUG" });
            await database.UpdateAdminAsync(target);
            foreach (var mode in new[] { "deny", "deny", "grant", "inherit" })
            {
                var result = await Call("staff_flags", new { Flag = "ban", Mode = mode });
                Assert.That(result.GetProperty("ok").GetBoolean(), Is.True);
                target = (await database.GetAdminDataForAsync(uid))!;
                Assert.That(target.AdminRankId, Is.EqualTo(trial.AdminRankId));
                Assert.That(target.Title, Is.EqualTo("Keep title"));
                Assert.That(target.Suspended, Is.True);
                Assert.That(target.Flags.Count(f => f.Flag == "DEBUG" && !f.Negative), Is.EqualTo(1));
                Assert.That(target.Flags.Count(f => f.Flag == "BAN"), Is.EqualTo(mode == "inherit" ? 0 : 1));
                Assert.That(result.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Contains("BAN"), Is.EqualTo(mode != "deny"));
            }
            var restricted = (await database.GetAdminDataForAsync(actor))!;
            restricted.Flags = new() { new AdminFlag { Flag = "PERMISSIONS" }, new AdminFlag { Flag = "BAN" } };
            await database.UpdateAdminAsync(restricted);
            Assert.That((await Call("staff_flags", new { Flag = "HOST", Mode = "grant" })).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
            Assert.That((await Call("staff_flags", new { Flag = "BAN", Mode = "deny" })).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
        }
        finally
        {
            await server.WaitPost(() => { enabled.SetValue(system, false); storeField.SetValue(system, null); });
        }
        await pair.CleanReturnAsync();
    }
}
