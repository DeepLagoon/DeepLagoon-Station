#nullable enable
using System.Linq;
using System.Reflection;
using System.Threading;
using Content.Server.Administration.Managers;
using Content.Server.Administration.UI;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Network;
using static Content.Shared.Administration.PermissionsEuiMsg;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PermissionsPanelTests
{
    [Test]
    public async Task OfflineRosterAndRankEditing()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var admins = server.ResolveDependency<IAdminManager>();
        var uid = new NetUserId(Guid.NewGuid());
        // This administrator has never connected and has no player-history row.
        await database.AddAdminAsync(new Admin { UserId = uid.UserId, Flags = new() });
        await database.AddAdminAsync(new Admin { UserId = pair.Player!.UserId.UserId,
            Flags = new() { new AdminFlag { Flag = "HOST" } } });
        await server.WaitPost(() => admins.ReloadAdmin(pair.Player!));
        await PoolManager.WaitUntil(server, () => admins.HasAdminFlag(pair.Player!, AdminFlags.Permissions));
        PermissionsEui ui = null!;
        await server.WaitPost(() =>
        {
            ui = new PermissionsEui();
            server.ResolveDependency<EuiManager>().OpenEui(ui, pair.Player!);
        });
        await PoolManager.WaitUntil(server, () => !((PermissionsEuiState) ui.GetNewState()).IsLoading);
        var state = (PermissionsEuiState) ui.GetNewState();
        Assert.That(state.Error, Is.Null);
        Assert.That(state.Admins.Single(a => a.UserId == uid).Online, Is.False);
        Assert.That(state.Admins.Single(a => a.UserId == uid).UserName, Is.Null);

        var gate = (SemaphoreSlim) typeof(PermissionsEui).GetField("_messageLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ui)!;
        async Task Send(EuiMessageBase message, string? error = null)
        {
            await server.WaitPost(() => ui.HandleMessage(message));
            await PoolManager.WaitUntil(server, () => gate.CurrentCount == 1);
            Assert.That(((PermissionsEuiState) ui.GetNewState()).Error, Is.EqualTo(error));
        }

        await Send(new AddAdminRank { Name = "Panel rank", ShortName = "PR", Flags = AdminFlags.Admin | AdminFlags.Ban });
        state = (PermissionsEuiState) ui.GetNewState();
        var rankId = state.AdminRanks.Single(r => r.Value.Name == "Panel rank").Key;
        await Send(new UpdateAdmin { UserId = uid, RankId = rankId, Title = "Offline admin", NegFlags = AdminFlags.Ban });
        var updated = (await database.GetAdminDataForAsync(uid))!;
        Assert.That(updated.AdminRankId, Is.EqualTo(rankId));
        Assert.That(updated.Flags.Single().Negative, Is.True);
        await Send(new UpdateAdmin { UserId = uid, RankId = int.MaxValue, Title = "Must not persist" }, "permissions-eui-rank-missing");
        Assert.That((await database.GetAdminDataForAsync(uid))!.Title, Is.EqualTo("Offline admin"));
        await Send(new UpdateAdminRank { Id = rankId, Name = "Edited rank", ShortName = "ER", Flags = AdminFlags.Admin });
        Assert.That(((PermissionsEuiState) ui.GetNewState()).AdminRanks[rankId].Name, Is.EqualTo("Edited rank"));
        await Send(new Refresh());
        state = (PermissionsEuiState) ui.GetNewState();
        await pair.Client.WaitPost(() =>
        {
            var clientUi = new Content.Client.Administration.UI.PermissionsEui();
            clientUi.HandleState(state);
            var menu = typeof(Content.Client.Administration.UI.PermissionsEui)
                .GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clientUi)!;
            var list = (Robust.Client.UserInterface.Controls.GridContainer) menu.GetType().GetField("AdminsList")!.GetValue(menu)!;
            Assert.That(list.ChildCount, Is.EqualTo((state.Admins.Length + 1) * 6));
            ((Robust.Client.UserInterface.CustomControls.DefaultWindow) menu).Dispose();
        });
        await Send(new RemoveAdmin { UserId = uid });
        Assert.That(await database.GetAdminDataForAsync(uid), Is.Null);
        await Send(new RemoveAdminRank { Id = rankId });
        Assert.That(await database.GetAdminRankAsync(rankId), Is.Null);
        await server.WaitPost(ui.Close);
        await pair.CleanReturnAsync();
    }
}
