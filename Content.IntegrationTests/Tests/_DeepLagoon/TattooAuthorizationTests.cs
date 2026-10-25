using System.Linq;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.Tattoos;
using Content.Shared.Preferences;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TattooAuthorizationTests
{
    [Test]
    public async Task TattooBanBlocksArtworkAndForgedProfileEditsAndCanBePardoned()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var user = default(NetUserId);
        var slot = 0;
        var original = new Tattoo();
        Task operation = Task.CompletedTask;
        await pair.Server.WaitPost(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single(); user = session.UserId;
            var prefs = pair.Server.ResolveDependency<IServerPreferencesManager>();
            slot = prefs.GetPreferences(user).SelectedCharacterIndex;
            var profile = (HumanoidCharacterProfile)prefs.GetPreferences(user).GetProfile(slot);
            var appearance = profile.Appearance.Clone(); appearance.Tattoos = [original];
            operation = prefs.SetProfile(user, slot, profile.WithCharacterAppearance(appearance), persist: false);
        });
        await operation;
        await pair.Server.WaitPost(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            operation = pair.Server.ResolveDependency<Content.Server.Administration.Managers.IBanManager>().CreateRoleBanAsync(
                user, session.Name, null, null, null, TattooAccess.BanRole, 0, Content.Shared.Database.NoteSeverity.Medium,
                "Tattoo access regression test", DateTimeOffset.UtcNow);
        });
        await pair.RunTicksSync(60); await operation; await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(pair.Client.ResolveDependency<Content.Client.Players.PlayTimeTracking.JobRequirementsManager>().TattoosBanned, Is.True);
            var uid = pair.Client.EntMan.SpawnEntity("MobHumanDummy", Robust.Shared.Map.MapCoordinates.Nullspace);
            try
            {
                using var state = System.Text.Json.JsonDocument.Parse(new Content.Client._DeepLagoon.Tattoos.TattooEditorData().Build(uid, []).ToString());
                Assert.That(state.RootElement.GetProperty("disabled").GetBoolean(), Is.True);
                Assert.That(state.RootElement.GetProperty("status").GetString(), Is.EqualTo(TattooAccess.BanMessage));
            }
            finally { pair.Client.EntMan.DeleteEntity(uid); }
        });
        Task<Tattoo> pending = null;
        await pair.Client.WaitPost(() => pending = pair.Client.EntMan.System<Content.Client._DeepLagoon.Tattoos.TattooArtworkSystem>().Authorize(new Tattoo()));
        await pair.RunTicksSync(60);
        Assert.That(pending.IsFaulted, Is.True);
        Assert.That(pending.Exception!.GetBaseException().Message, Is.EqualTo(TattooAccess.BanMessage));
        await pair.Server.WaitPost(() =>
        {
            var prefs = pair.Server.ResolveDependency<IServerPreferencesManager>();
            var profile = (HumanoidCharacterProfile)prefs.GetPreferences(user).GetProfile(slot);
            var appearance = profile.Appearance.Clone(); appearance.Tattoos = [new Tattoo()];
            operation = prefs.SetProfile(user, slot, profile.WithCharacterAppearance(appearance), authoritative: false, persist: false);
            var body = pair.Server.EntMan.SpawnEntity("MobHuman", Robust.Shared.Map.MapCoordinates.Nullspace);
            var session = pair.Server.PlayerMan.Sessions.Single(); pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(pair.Server.EntMan.System<Content.Server._DeepLagoon.Tattoos.TattooMachineSystem>().TattoosAllowed(body), Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(session, null); pair.Server.EntMan.DeleteEntity(body);
        });
        await operation;
        await pair.Server.WaitAssertion(() =>
        {
            var profile = (HumanoidCharacterProfile)pair.Server.ResolveDependency<IServerPreferencesManager>().GetPreferences(user).GetProfile(slot);
            Assert.That(profile.Appearance.Tattoos.Single().ContentEquals(original), Is.True, "A raw profile packet must preserve stored ink while banned.");
        });
        var records = await pair.Server.ResolveDependency<Content.Server.Database.IServerDbManager>().GetServerRoleBansAsync(null, user, null, null);
        var id = records.Single(b => b.Role == TattooAccess.BanRole).Id!.Value;
        await pair.Server.WaitPost(() => operation = pair.Server.ResolveDependency<Content.Server.Administration.Managers.IBanManager>().PardonRoleBan(id, null, DateTimeOffset.UtcNow));
        await pair.RunTicksSync(60); await operation; await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() => Assert.That(pair.Client.ResolveDependency<Content.Client.Players.PlayTimeTracking.JobRequirementsManager>().TattoosBanned, Is.False));
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<Content.Server._DeepLagoon.Tattoos.TattooArtworkSystem>().IsBanned(user), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ForgedImportIsRejectedOverGameChannelAndCannotEnterPreferences()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitPost(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            if (pair.Server.ResolveDependency<Content.Server.Administration.Managers.IAdminManager>().GetAdminData(session, true) is { } admin)
                admin.Flags &= ~Content.Shared.Administration.AdminFlags.Host;
        });
        var forged = new Tattoo { NormalBitmap = Content.Tests._DeepLagoon.TattooTests.Bitmap(),
            ArtworkProof = Guid.NewGuid().ToString("N") + ":" + new string('0', 64) };
        Task<Tattoo> pending = null;
        await pair.Client.WaitPost(() => pending = pair.Client.EntMan.System<Content.Client._DeepLagoon.Tattoos.TattooArtworkSystem>().Authorize(forged));
        await pair.RunTicksSync(100);
        Assert.That(pending.IsFaulted, Is.True);
        Assert.That(pending.Exception!.GetBaseException().Message, Does.Contain("третий тир"));
        Task save = Task.CompletedTask;
        NetUserId user = default; int slot = 0;
        await pair.Server.WaitPost(() =>
        {
            var preferences = pair.Server.ResolveDependency<IServerPreferencesManager>();
            user = pair.Server.PlayerMan.Sessions.Single().UserId;
            slot = preferences.GetPreferences(user).SelectedCharacterIndex;
            var profile = (HumanoidCharacterProfile)preferences.GetPreferences(user).GetProfile(slot);
            var appearance = profile.Appearance.Clone(); appearance.Tattoos = [forged];
            save = preferences.SetProfile(user, slot, profile.WithCharacterAppearance(appearance), authoritative: false, persist: false);
        });
        await pair.RunTicksSync(20); await save;
        await pair.Server.WaitAssertion(() =>
        {
            var profile = (HumanoidCharacterProfile)pair.Server.ResolveDependency<IServerPreferencesManager>().GetPreferences(user).GetProfile(slot);
            Assert.That(profile.Appearance.Tattoos.Any(t => t.NormalBitmap == forged.NormalBitmap), Is.False,
                "Bypassing the importer UI and sending profile bitmap data must still fail");
        });
        await pair.CleanReturnAsync();
    }
}
