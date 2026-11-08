using System.Linq;
using System.Text.RegularExpressions;
using Content.Client._DeepLagoon.Currency;
using Content.Client.UserInterface.Systems.Chat;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.Administration;
using Content.Shared.Chat;
using Robust.Client.UserInterface;
using Robust.Shared.Asynchronous;
using Robust.Shared.Localization;
using ServerWallet = Content.Server._DeepLagoon.Currency.LagoonCoinSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LagoonCoinTransferTests
{
    [Test]
    public async Task ChatTransferRequiresConfirmationAndOnlyPaysOnce()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Destructive = true,
            Connected = true,
            DummyTicker = false,
        });
        var sender = pair.Player!;
        var recipient = await pair.Server.AddDummySession("LCRecipient");
        var ticker = pair.Server.System<GameTicker>();
        await PoolManager.WaitUntil(pair.Server, () => ticker.UserHasJoinedGame(recipient), 600);
        var database = pair.Server.ResolveDependency<IServerDbManager>();
        var chat = pair.Client.ResolveDependency<IUserInterfaceManager>().GetUIController<ChatUIController>();
        var balance = pair.Client.System<LagoonCoinSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            var admins = pair.Server.ResolveDependency<IAdminManager>();
            if (admins.HasAdminFlag(sender, AdminFlags.None))
                admins.DeAdmin(sender);
            Assert.That(admins.HasAdminFlag(sender, AdminFlags.None), Is.False);

            var tasks = pair.Server.ResolveDependency<ITaskManager>();
            tasks.BlockWaitOnTask(pair.Server.System<ServerWallet>().Grant(sender.UserId, 10, "Transfer test", null));
        });

        async Task Send(string command)
        {
            await pair.Client.WaitPost(() =>
            {
                var box = chat.Chats.First();
                box.ChatInput.Input.SetText(command);
                chat.SendMessage(box, ChatSelectChannel.Local);
            });
            await pair.RunTicksSync(10);
        }

        await Send($"/lc:pay {recipient.Name} 7");
        var confirmation = string.Empty;
        await pair.Client.WaitAssertion(() =>
        {
            var pattern = new Regex("/lc:pay [0-9a-f]{8}");
            var matches = chat.History.Where(entry => entry.Msg.Channel == ChatChannel.Server)
                .Select(entry => pattern.Match(entry.Msg.Message))
                .Where(match => match.Success).ToArray();
            Assert.That(matches, Has.Length.EqualTo(1));
            confirmation = matches[0].Value;
            Assert.That(balance.Balance, Is.EqualTo(10));
        });
        Assert.That(await database.GetLagoonCoinsAsync(recipient.UserId), Is.Zero);

        await Send(confirmation);
        await Send(confirmation);
        await pair.Client.WaitAssertion(() => Assert.That(balance.Balance, Is.EqualTo(3)));
        Assert.That(await database.GetLagoonCoinsAsync(recipient.UserId), Is.EqualTo(7));
        var debit = (await database.GetLagoonCoinHistoryAsync(sender.UserId, 1)).Single(row => row.Amount == -7);
        var credit = (await database.GetLagoonCoinHistoryAsync(recipient.UserId, 1)).Single();
        Assert.That(credit.OperationId, Is.EqualTo(debit.OperationId));

        await pair.Server.WaitPost(() => ticker.EndRound());
        await Send($"/lc:pay {recipient.Name} 2");
        await pair.Client.WaitAssertion(() =>
        {
            var rejection = Loc.GetString("lagoon-coin-pay-in-round-only");
            Assert.That(chat.History.Any(entry => entry.Msg.Channel == ChatChannel.Server &&
                                                  entry.Msg.Message == rejection), Is.True);
        });

        await pair.CleanReturnAsync();
    }
}
