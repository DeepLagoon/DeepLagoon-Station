#nullable enable
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using DbAdmin = Content.Server.Database.Admin;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DiscordApartmentTests
{
    [TestPrototypes]
    private const string Templates = @"
- type: apartmentTemplate
  id: DiscordApartmentPaidTest
  name: dl-apartment-basic
  price: 500
  map: /Maps/_DeepLagoon/Apartments/apartments_basic.yml
  width: 11
  height: 16
  arrival: '4,8'
  reservedCells: ['3,12', '4,12', '5,12', '3,13', '4,13', '5,13', '3,14', '4,14', '5,14', '4,10', '4,11']
  catalog: [DLAptChair]
- type: apartmentTemplate
  id: DiscordApartmentBrokenTest
  name: dl-apartment-basic
  map: /Maps/_DeepLagoon/Apartments/apartments_basic.yml
  width: 5
  height: 5
";

    [Test]
    public async Task OfflineHostGrantsArePersistentIdempotentAndPreserveSelectedLayout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var system = server.EntMan.System<DiscordLinkSystem>();
        var apartments = server.EntMan.System<ApartmentSystem>();
        var uid = new NetUserId(Guid.NewGuid());
        var actor = new NetUserId(Guid.NewGuid());
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var enabled = typeof(DiscordLinkSystem).GetField("_enabled", flags)!;
        var requestType = typeof(DiscordLinkSystem).GetNestedType("ModerationRequest", BindingFlags.NonPublic)!;
        var moderate = typeof(DiscordLinkSystem).GetMethod("Moderate", flags)!;
        IApartmentLayoutStore store = null!;
        await server.WaitPost(() =>
        {
            enabled.SetValue(system, true);
            store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", flags)!.GetValue(apartments)!;
        });
        try
        {
            await PoolManager.WaitUntil(server, async () =>
            {
                await database.UpdatePlayerRecordAsync(uid, "ApartmentRecipient", IPAddress.Loopback, null);
                await database.UpdatePlayerRecordAsync(actor, "ApartmentHost", IPAddress.Loopback, null);
                return true;
            });
            async Task<JsonElement> Call(string operation, string template = "DiscordApartmentPaidTest", string reason = "test grant")
            {
                var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Operation=operation,
                    Uid=uid.ToString(), ActorUid=actor.ToString(), Template=template, Message=reason }), requestType)!;
                Task task = null!;
                await server.WaitPost(() => task = (Task) moderate.Invoke(system, new[] { request, (object) uid, actor })!);
                await PoolManager.WaitUntil(server, () => task.IsCompleted);
                await task;
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                return JsonSerializer.SerializeToElement(result.GetType().GetProperty("Body")!.GetValue(result));
            }
            async Task SetAdmin(bool suspended, params AdminFlag[] adminFlags) => await PoolManager.WaitUntil(server, async () =>
            {
                var admin = await database.GetAdminDataForAsync(actor);
                if (admin == null) await database.AddAdminAsync(new DbAdmin { UserId=actor.UserId, Suspended=suspended, Flags=adminFlags.ToList() });
                else { admin.Suspended=suspended; admin.Flags=adminFlags.ToList(); await database.UpdateAdminAsync(admin); }
                return true;
            });
            Assert.That((await Call("apartment_catalog")).GetProperty("error").GetString(), Is.EqualTo("apartment_host_required"));
            await SetAdmin(false, new AdminFlag { Flag="PERMISSIONS" }, new AdminFlag { Flag="ADMIN" });
            Assert.That((await Call("apartment_grant")).GetProperty("error").GetString(), Is.EqualTo("apartment_host_required"));
            await SetAdmin(true, new AdminFlag { Flag="HOST" });
            Assert.That((await Call("apartment_catalog")).GetProperty("error").GetString(), Is.EqualTo("apartment_host_required"));
            await SetAdmin(false, new AdminFlag { Flag="HOST", Negative=true });
            Assert.That((await Call("apartment_catalog")).GetProperty("error").GetString(), Is.EqualTo("apartment_host_required"));
            await SetAdmin(false, new AdminFlag { Flag="HOST" });
            var catalog = (await Call("apartment_catalog")).GetProperty("rows").EnumerateArray().ToArray();
            Assert.That(catalog.Single(row => row.GetProperty("id").GetString() == "DiscordApartmentPaidTest").GetProperty("price").GetInt64(), Is.EqualTo(500));
            Assert.That(catalog.Single(row => row.GetProperty("id").GetString() == "DLApartmentStudio").GetProperty("available").GetBoolean(), Is.False);
            foreach (var id in new[] { "missing", "DLApartmentStudio" })
                Assert.That((await Call("apartment_grant", id)).GetProperty("error").GetString(), Is.EqualTo("apartment_unavailable"));
            Assert.That((await Call("apartment_grant", reason:" ")).GetProperty("error").GetString(), Is.EqualTo("invalid_request"));
            Assert.That((await Call("apartment_grant", "DiscordApartmentBrokenTest")).GetProperty("error").GetString(), Is.EqualTo("apartment_invalid_map"));
            string before = "";
            await server.WaitAssertion(() =>
            {
                store.Select(uid.UserId, server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic"));
                var current = store.Load(uid.UserId)!;
                var draft = new ApartmentDelta { Template=current.Template, TemplateVersion=current.TemplateVersion,
                    Revision=current.Revision+1, Added=[new ApartmentPlacement { Id="keep-chair", Furniture="DLAptChair", X=2, Y=2 }] };
                Assert.That(store.Save(uid.UserId, draft, current.Revision), Is.True);
                before = JsonSerializer.Serialize(store.Load(uid.UserId));
            });
            var result = await Call("apartment_grant");
            Assert.That(result.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("already_owned").GetBoolean(), Is.False);
            Assert.That((await Call("apartment_grant")).GetProperty("already_owned").GetBoolean(), Is.True);
            await server.WaitAssertion(() =>
            {
                Assert.That(store.Owned(uid.UserId, "DiscordApartmentPaidTest"), Is.True);
                Assert.That(store.Owned(uid.UserId, "DiscordApartmentBrokenTest"), Is.False);
                Assert.That(JsonSerializer.Serialize(store.Load(uid.UserId)), Is.EqualTo(before));
            });
        }
        finally { await server.WaitPost(() => enabled.SetValue(system, false)); }
        await pair.CleanReturnAsync();
    }
}
