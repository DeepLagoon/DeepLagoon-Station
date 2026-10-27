#nullable enable
using System.IO;
using System.Linq;
using Content.Client.Lobby;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.CharacterInfo;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.ContentPack;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.IntegrationTests.Tests._DeepLagoon;
[TestFixture, NonParallelizable]
public sealed class HeadshotTransferTests
{
    [Test]
    public async Task PersistentCacheStaysWithinBudget()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var system = pair.Client.ResolveDependency<IEntityManager>().System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>();
            var cache = system.GetType().GetMethod("Cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var image = new byte[HeadshotLimits.SponsorMaxBytes];
            for (var i = 0; i < 7; i++) cache.Invoke(system, new object[] { Guid.NewGuid().ToString("N"), image, "image/png" });
            var resources = pair.Client.ResolveDependency<IResourceManager>();
            var directory = new Robust.Shared.Utility.ResPath("/headshot_cache");
            var total = 0L; var count = 0;
            foreach (var name in resources.UserData.DirectoryEntries(directory))
            {
                using var file = resources.UserData.Open(directory / name, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var buffer = new MemoryStream(); file.CopyTo(buffer);
                total += buffer.Length; count++;
            }
            Assert.That(total, Is.LessThanOrEqualTo(32 * 1024 * 1024));
            Assert.That(count, Is.EqualTo(6));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UploadReadReplaceAndRejectForeignSlotOverGameChannel()
    {
        await using var pair=await PoolManager.GetServerClient(new PoolSettings {Destructive=true,InLobby=true});
        var client=pair.Client;var server=pair.Server;
        await server.WaitPost(() =>
        {
            var session = server.ResolveDependency<Robust.Server.Player.IPlayerManager>().Sessions.Single();
            if (server.ResolveDependency<Content.Server.Administration.Managers.IAdminManager>().GetAdminData(session, true) is { } admin)
                admin.Flags &= ~Content.Shared.Administration.AdminFlags.Host;
        });
        using var picture=new Image<Rgba32>(32,32);using var png=new MemoryStream();picture.SaveAsPng(png);var original=png.ToArray();
        using var jpeg=new MemoryStream();picture.SaveAsJpeg(jpeg);var replacement=jpeg.ToArray();
        var cleanOriginal = Content.Server._DeepLagoon.CharacterInfo.HeadshotImage.Normalize(original, false).Bytes;
        var cleanReplacement = Content.Server._DeepLagoon.CharacterInfo.HeadshotImage.Normalize(replacement, false).Bytes;
        int slot=0;string firstId="";byte[]? read=null;string error="";
        await client.WaitPost(()=>
        {
            slot=client.ResolveDependency<IClientPreferencesManager>().Preferences!.SelectedCharacterIndex;
            var net=client.ResolveDependency<IEntityManager>().EntityNetManager!;var request=Guid.NewGuid();
            net.SendSystemNetworkMessage(new HeadshotBeginEvent(request,slot,original.Length));
            net.SendSystemNetworkMessage(new HeadshotUploadEvent(request,0,original));
        });
        await pair.RunTicksSync(200);
        await server.WaitAssertion(()=>
        {
            var session=server.ResolveDependency<Robust.Server.Player.IPlayerManager>().Sessions.Single();
            var profile=(HumanoidCharacterProfile)server.ResolveDependency<IServerPreferencesManager>().GetPreferences(session.UserId).GetProfile(slot);
            firstId=profile.HeadshotId;Assert.That(Guid.TryParse(firstId,out _),Is.True);

        });
        await client.WaitPost(()=>client.ResolveDependency<IEntityManager>().System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>().Read(slot,null,(bytes,_,_,message)=>{read=bytes;error=message;}));
        await pair.RunTicksSync(20);
        Assert.That(error,Is.Empty);Assert.That(read,Is.EqualTo(cleanOriginal));
        HeadshotGalleryDataEvent? gallery = null;
        await client.WaitPost(() => client.ResolveDependency<IEntityManager>().System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>().Gallery(slot, null, ev => gallery = ev));
        await pair.RunTicksSync(20);
        Assert.That(gallery, Is.Not.Null);
        Assert.That(gallery!.Images.Length, Is.EqualTo(1));
        Assert.That(gallery.ActiveCapacity, Is.EqualTo(1));
        var receivedBytes = 0; var notModified = false;
        EventHandler<object> observer = (_, message) => { if (message is HeadshotDataEvent ev) { receivedBytes += ev.Bytes.Length; notModified |= ev.NotModified; } };
        await client.WaitPost(() =>
        {
            var entities = client.ResolveDependency<IEntityManager>();
            entities.EntityNetManager!.ReceivedSystemMessage += observer;
            var profile = (HumanoidCharacterProfile)client.ResolveDependency<IClientPreferencesManager>().Preferences!.Characters[slot];
            profile.HeadshotId = firstId;
        });
        await pair.RunTicksSync(25);
        read = null;
        await client.WaitAssertion(() =>
        {
            client.ResolveDependency<IEntityManager>().System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>().Read(slot, null, (bytes, _, _, message) => { read = bytes; error = message; });
            Assert.That(read, Is.EqualTo(cleanOriginal), "Own cached image should be available immediately");
        });
        await pair.RunTicksSync(25);
        Assert.That(notModified, Is.True); Assert.That(receivedBytes, Is.Zero);
        // Discard the memory cache to exercise the persistent client cache as after a restart.
        await client.WaitAssertion(() =>
        {
            var system = client.ResolveDependency<IEntityManager>().System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>();
            var cache = (System.Collections.IDictionary)system.GetType().GetField("_cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(system)!;
            cache.Clear(); read = null;
            system.Read(slot, null, (bytes, _, _, message) => { read = bytes; error = message; });
            Assert.That(read, Is.EqualTo(cleanOriginal));
        });
        await pair.RunTicksSync(25);
        Assert.That(receivedBytes, Is.Zero);
        await client.WaitPost(()=>
        {
            var net=client.ResolveDependency<IEntityManager>().EntityNetManager!;var request=Guid.NewGuid();
            net.SendSystemNetworkMessage(new HeadshotBeginEvent(request,slot,replacement.Length));net.SendSystemNetworkMessage(new HeadshotUploadEvent(request,0,replacement));
        });
        await pair.RunTicksSync(200);
        await server.WaitAssertion(()=>
        {
            var session=server.ResolveDependency<Robust.Server.Player.IPlayerManager>().Sessions.Single();
            var profile=(HumanoidCharacterProfile)server.ResolveDependency<IServerPreferencesManager>().GetPreferences(session.UserId).GetProfile(slot);
            Assert.That(profile.HeadshotId,Is.EqualTo(firstId));

        });
        read=null;
        await client.WaitPost(()=>client.ResolveDependency<IEntityManager>().System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>().Read(slot,null,(bytes,_,_,message)=>{read=bytes;error=message;}));
        await pair.RunTicksSync(20);Assert.That(error,Is.Empty);Assert.That(read,Is.EqualTo(cleanReplacement));
        Assert.That(receivedBytes, Is.GreaterThan(0), "Replacement must invalidate the cached content hash");
        await client.WaitPost(()=>
        {
            var net=client.ResolveDependency<IEntityManager>().EntityNetManager!;var request=Guid.NewGuid();
            net.SendSystemNetworkMessage(new HeadshotBeginEvent(request,999,original.Length));net.SendSystemNetworkMessage(new HeadshotUploadEvent(request,0,original));
        });
        await pair.RunTicksSync(20);
        await server.WaitAssertion(()=>
        {
            var session=server.ResolveDependency<Robust.Server.Player.IPlayerManager>().Sessions.Single();
            var profile=(HumanoidCharacterProfile)server.ResolveDependency<IServerPreferencesManager>().GetPreferences(session.UserId).GetProfile(slot);
            Assert.That(profile.HeadshotId,Is.EqualTo(firstId));
        });
        await client.WaitPost(() => client.ResolveDependency<IEntityManager>().EntityNetManager!.ReceivedSystemMessage -= observer);
        await pair.CleanReturnAsync();
    }
}
