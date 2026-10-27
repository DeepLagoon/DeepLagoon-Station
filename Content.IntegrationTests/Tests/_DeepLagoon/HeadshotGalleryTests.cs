#nullable enable
using System.IO;
using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.CharacterInfo;
using Content.Shared.Preferences;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.ContentPack;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ServerHeadshots = Content.Server._DeepLagoon.CharacterInfo.HeadshotSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;
[TestFixture, NonParallelizable]
public sealed class HeadshotGalleryTests
{
    [Test]
    public async Task LibraryEnforcesOwnershipLimitsAndPersistsSelections()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        await server.WaitPost(() =>
        {
            var config = server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CCVars.AutoVoteEnabled, false);
            config.SetCVar(CCVars.MapAutoVoteEnabled, false);
            config.SetCVar(CCVars.PresetAutoVoteEnabled, false);
            config.SetCVar(CCVars.VoteEnabled, false);
            var votes = server.ResolveDependency<Content.Server.Voting.Managers.IVoteManager>();
            foreach (var vote in votes.ActiveVotes.ToArray())
                vote.Cancel();
            votes.Update();
        });
        var session = await server.AddDummySession();
        await PoolManager.WaitUntil(server, () => session.Status == Robust.Shared.Enums.SessionStatus.InGame, 600);
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var linking = server.ResolveDependency<IEntityManager>().System<DiscordLinkSystem>();
        var system = server.ResolveDependency<IEntityManager>().System<ServerHeadshots>();
        var storeField = typeof(DiscordLinkSystem).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var enabledField = typeof(DiscordLinkSystem).GetField("_enabled", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousStore = storeField.GetValue(linking); var previousEnabled = enabledField.GetValue(linking);
        var directory = Path.Combine(Path.GetTempPath(), "ss14-gallery-" + Guid.NewGuid());
        using var store = new DiscordLinkStore(Path.Combine(directory, "links.db"));
        var auth = session.GetType().GetProperty("AuthType")!; var previousAuth = auth.GetValue(session);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sender = new EntitySessionEventArgs(session);
        int slot = 0;
        ServerHeadshots.ImageGallery? gallery = null;
        string rootId = "";
        void Reload()
        {
            var profile = (HumanoidCharacterProfile)preferences.GetPreferences(session.UserId).GetProfile(slot);
            rootId = profile.HeadshotId;
            gallery = (ServerHeadshots.ImageGallery)typeof(ServerHeadshots).GetMethod("LoadGallery", flags)!.Invoke(system, [rootId])!;
        }
        try
        {
            await server.WaitPost(() =>
            {
                slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
                if (server.ResolveDependency<Content.Server.Administration.Managers.IAdminManager>().GetAdminData(session, true) is { } admin)
                    admin.Flags &= ~Content.Shared.Administration.AdminFlags.Host;
                storeField.SetValue(linking, store); enabledField.SetValue(linking, true); auth.SetValue(session, LoginType.LoggedIn);
                store.Consume("1554565156657299597", store.Issue(session.UserId.UserId, session.Name));
                store.SetSupporter("1554565156657299597", false, 2, [0xffffff, 0xffffff, 0xffffff, 0xffffff]);
                Assert.That(typeof(ServerHeadshots).GetMethod("Capacity", flags)!.Invoke(system, [session]), Is.EqualTo(5));
                Assert.That(typeof(ServerHeadshots).GetMethod("ActiveCapacity", flags)!.Invoke(system, [session]), Is.EqualTo(3));
            });
            async Task Upload(bool append, byte red)
            {
                using var picture = new Image<Rgba32>(16, 16); picture[0, 0] = new Rgba32(red, 0, 0);
                using var png = new MemoryStream(); picture.SaveAsPng(png); var bytes = png.ToArray();
                await server.WaitPost(() =>
                {
                    var request = Guid.NewGuid();
                    typeof(ServerHeadshots).GetMethod("Begin", flags)!.Invoke(system, [new HeadshotBeginEvent(request, slot, bytes.Length, append), sender]);
                    typeof(ServerHeadshots).GetMethod("Chunk", flags)!.Invoke(system, [new HeadshotUploadEvent(request, 0, bytes), sender]);
                });
                await pair.RunTicksSync(200);
                await server.WaitAssertion(Reload);
            }
            async Task Change(string action, string id)
            {
                await pair.RunTicksSync(20); // Respect the production gallery mutation cooldown.
                await server.WaitAssertion(() =>
                {
                    typeof(ServerHeadshots).GetMethod("Gallery", flags)!.Invoke(system, [new HeadshotGalleryRequestEvent(Guid.NewGuid(), slot, action: action, imageId: id), sender]);
                    Reload();
                });
            }
            await Upload(false, 0); await Upload(true, 255);
            Assert.That(gallery!.Images.Count, Is.EqualTo(2)); Assert.That(gallery.Active.Count, Is.EqualTo(2));
            var second = gallery.Images[1];
            await Change("primary", second); Assert.That(gallery!.Active[0], Is.EqualTo(second));
            long beforeNoOp = 0;
            await server.WaitAssertion(() =>
            {
                beforeNoOp = (long)typeof(ServerHeadshots).GetField("_imageRevision", flags)!.GetValue(system)!;
                server.ResolveDependency<IResourceManager>().UserData.WriteAllText(new Robust.Shared.Utility.ResPath("/headshots/" + rootId + ".json.bak"), "no-op sentinel");
            });
            await Change("primary", second);
            await server.WaitAssertion(() =>
            {
                Assert.That((long)typeof(ServerHeadshots).GetField("_imageRevision", flags)!.GetValue(system)!, Is.EqualTo(beforeNoOp), "No-op primary cannot rewrite image alias");
                Assert.That(server.ResolveDependency<IResourceManager>().UserData.ReadAllText(new Robust.Shared.Utility.ResPath("/headshots/" + rootId + ".json.bak")), Is.EqualTo("no-op sentinel"), "No-op primary cannot rewrite manifest/backup");
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(system.GetActive(rootId)[0], Is.EqualTo(second), "A fresh manifest read must restore the primary selection");
                var resources = server.ResolveDependency<IResourceManager>();
                using var primary = resources.UserData.Open(ServerHeadshots.PathFor(rootId), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var selected = resources.UserData.Open(ServerHeadshots.PathFor(second), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var a = new MemoryStream(); using var b = new MemoryStream(); primary.CopyTo(a); selected.CopyTo(b);
                Assert.That(a.ToArray(), Is.EqualTo(b.ToArray()));
            });
            await Change("delete", Guid.NewGuid().ToString("N")); Assert.That(gallery!.Images.Count, Is.EqualTo(2));
            for (byte i = 1; i <= 3; i++) await Upload(true, i);
            Assert.That(gallery!.Images.Count, Is.EqualTo(5)); Assert.That(gallery.Active.Count, Is.EqualTo(3));
            await Upload(true, 4); Assert.That(gallery!.Images.Count, Is.EqualTo(5), "A sixth image must be rejected on tier 2");
            await Change("delete", second); Assert.That(gallery!.Images, Does.Not.Contain(second));
            await server.WaitAssertion(() =>
            {
                Reload(); Assert.That(gallery!.Images.Count, Is.EqualTo(4));
                store.SetSupporter("1554565156657299597", false, 3, [0xffffff, 0xffffff, 0xffffff, 0xffffff]);
                Assert.That(typeof(ServerHeadshots).GetMethod("Capacity", flags)!.Invoke(system, [session]), Is.EqualTo(10));
                Assert.That(typeof(ServerHeadshots).GetMethod("ActiveCapacity", flags)!.Invoke(system, [session]), Is.EqualTo(4));
                system.Delete(rootId);
                Assert.That(server.ResolveDependency<IResourceManager>().UserData.Exists(ServerHeadshots.PathFor(rootId)), Is.False);
            });
        }
        finally
        {
            await server.WaitPost(() => { auth.SetValue(session, previousAuth); enabledField.SetValue(linking, previousEnabled); storeField.SetValue(linking, previousStore); });
            await server.RemoveDummySession(session);
        }
        await pair.CleanReturnAsync();
    }
}
