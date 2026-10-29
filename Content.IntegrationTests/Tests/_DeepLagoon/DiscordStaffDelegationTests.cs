#nullable enable
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._DeepLagoon.DiscordLink;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using DbAdmin = Content.Server.Database.Admin;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DiscordStaffDelegationTests
{
    [TestCase("1554887617831764060", "admin")]
    [TestCase("1554887617831764060", "trial")]
    [TestCase("1555959349615263804", "mapper")]
    public async Task DepartmentHeadCanGrantOnlyItsStaffKindWithoutGamePermissions(string role, string kind)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var system = server.ResolveDependency<IEntityManager>().System<DiscordLinkSystem>();
        var uid = new NetUserId(Guid.NewGuid());
        var actor = new NetUserId(Guid.NewGuid());
        var path = Path.Combine(Path.GetTempPath(), "discord-staff-" + Guid.NewGuid(), "links.db");
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
                foreach (var id in new[] { uid, actor })
                    await database.UpdatePlayerRecordAsync(id, "DiscordStaff" + id, IPAddress.Loopback, null);
                await database.AddAdminRankAsync(new AdminRank { Name = "Game Admin", ShortName = "GA",
                    Flags = new[] { "ADMIN", "BAN", "VIEWNOTES" }.Select(f => new AdminRankFlag { Flag = f }).ToList() });
                return true;
            });
            async Task<JsonElement> Call(string operation, string? authority, string? selectedKind = null, bool wrongContext = false)
            {
                var data = new { Operation = operation, Uid = uid.ToString(), ActorUid = actor.ToString(),
                    Kind = selectedKind ?? kind, ActorStaffRole = authority,
                    StaffApplicationId = (kind == "trial") != wrongContext ? "1565299000000000000" : null };
                var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(data), requestType)!;
                Task task = null!;
                await server.WaitPost(() => task = (Task) moderate.Invoke(system, new[] { request, (object) uid, actor })!);
                await PoolManager.WaitUntil(server, () => task.IsCompleted);
                await task;
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                return JsonSerializer.SerializeToElement(result.GetType().GetProperty("Body")!.GetValue(result));
            }
            Assert.That(await database.GetAdminDataForAsync(actor), Is.Null);
            Assert.That((await Call("staff_add", null)).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
            Assert.That((await Call("staff_add", role, kind == "mapper" ? "admin" : "mapper")).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
            Assert.That((await Call("ban", role)).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
            Assert.That((await Call("staff_flags", role)).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
            Assert.That((await Call("staff_add", role)).GetProperty("ok").GetBoolean(), Is.True);
            Assert.That((await Call("staff_status", role)).GetProperty("kind").GetString(), Is.EqualTo(kind));
            Assert.That(await database.GetAdminDataForAsync(actor), Is.Null);
            Assert.That(store.StaffGrant(uid.UserId)!.Value.Kind, Is.EqualTo(kind));

            var admin = new DbAdmin { UserId = actor.UserId, Flags = new() { new AdminFlag { Flag = "PERMISSIONS" } } };
            await database.AddAdminAsync(admin);
            Assert.That((await Call("staff_add", role, wrongContext: true)).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
            admin = (await database.GetAdminDataForAsync(actor))!;
            admin.Suspended = true;
            await database.UpdateAdminAsync(admin);
            Assert.That((await Call("staff_status", role)).GetProperty("error").GetString(), Is.EqualTo("permissions_denied"));
        }
        finally
        {
            await server.WaitPost(() => { enabled.SetValue(system, false); storeField.SetValue(system, null); });
        }
        await pair.CleanReturnAsync();
    }
}
