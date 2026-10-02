using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DiscordLobbyAdmissionTests
{
    [Test]
    public async Task LinkingPrecedesWhitelistAndAllLobbyEntryPathsAreBlocked()
    {
        // Server-only test: no client guidebook/UI content is needed for admission.
        var (server, log) = await PoolManager.GenerateServer(new PoolSettings { InLobby = true }, TestContext.Out);
        using var instance = server;
        var systems = server.ResolveDependency<IEntitySystemManager>();
        var linking = systems.GetEntitySystem<DiscordLinkSystem>();
        var ticker = systems.GetEntitySystem<GameTicker>();
        var config = server.ResolveDependency<IConfigurationManager>();
        var database = server.ResolveDependency<IServerDbManager>();
        var directory = Path.Combine(Path.GetTempPath(), "ss14-lobby-admission-" + Guid.NewGuid());
        using var store = new DiscordLinkStore(Path.Combine(directory, "links.db"));
        var storeField = typeof(DiscordLinkSystem).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await server.WaitPost(() =>
        {
            // The integration server has no persistent data root; use an isolated test database.
            storeField.SetValue(linking, store);
            config.SetCVar(CCVars.DiscordLinkEnabled, true);
            // Dummy sessions do not implement voting's direct channel.SendMessage transport.
            config.SetCVar(CCVars.AutoVoteEnabled, false);
            config.SetCVar(CCVars.MapAutoVoteEnabled, false);
            config.SetCVar(CCVars.PresetAutoVoteEnabled, false);
            config.SetCVar(CCVars.VoteEnabled, false);
            ticker.RestartRound();
        });
        var session = await server.AddDummySession();
        await PoolManager.WaitUntil(server, () => session.Status == SessionStatus.InGame, 600);
        var auth = session.GetType().GetProperty("AuthType")!;
        try
        {
            await server.WaitAssertion(() =>
            {
                auth.SetValue(session, LoginType.LoggedIn);
                Assert.That(linking.CanEnterRound(session), Is.False);
                ticker.ToggleReady(session, true);
                ticker.ToggleReadyAll(true);
                ticker.MakeJoinGame(session, EntityUid.Invalid);
                ticker.JoinAsObserver(session);
                ticker.SpawnObserver(session);
                ticker.PlayerJoinGame(session);
                Assert.That(ticker.PlayerGameStatuses[session.UserId], Is.EqualTo(PlayerGameStatus.NotReadyToPlay));
                Assert.That(ticker.ReadyPlayerCount(), Is.Zero);
                Assert.That(session.AttachedEntity, Is.Null);
                var code = store.Issue(session.UserId.UserId, session.Name);
                store.Consume("1554565156657299597", code);
            });
            Task pending = Task.CompletedTask;
            await server.WaitPost(() => pending = linking.RefreshAdmission(session));
            await PoolManager.WaitUntil(server, () => pending.IsCompleted, 600);
            await pending;
            await server.WaitAssertion(() => Assert.That(linking.CanEnterRound(session), Is.False,
                "Linking alone must not grant access to the round"));
            async Task Approve()
            {
                await database.AddToWhitelistAsync(session.UserId);
                await linking.RefreshAdmission(session);
            }
            await server.WaitPost(() => pending = Approve());
            await PoolManager.WaitUntil(server, () => pending.IsCompleted, 600);
            await pending;
            await server.WaitAssertion(() =>
            {
                Assert.That(linking.CanEnterRound(session), Is.True,
                    "A linked and approved player gains admission without reconnecting");
                config.SetCVar(CCVars.GameLobbyEnabled, false);
                auth.SetValue(session, LoginType.GuestAssigned);
                Assert.That(linking.CanEnterRound(session), Is.False,
                    "Guest sessions cannot inherit approval, even when the normal lobby is disabled");
                ticker.ToggleReadyAll(true);
                Assert.That(ticker.ReadyPlayerCount(), Is.Zero);
            });
        }
        finally
        {
            await server.RemoveDummySession(session);
            await server.WaitPost(() =>
            {
                config.SetCVar(CCVars.DiscordLinkEnabled, false);
                storeField.SetValue(linking, null);
            });
            log.ShuttingDown = true;
            store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
