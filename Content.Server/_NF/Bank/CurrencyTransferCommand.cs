using System.Linq;
using Content.Server.Chat.Managers;
using Content.Shared.Administration;
using Content.Shared.Chat;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._NF.Bank;

/// <summary>
/// Player command for transferring funds between selected character accounts.
/// </summary>
[AnyCommand]
public sealed partial class CurrencyTransferCommand : LocalizedCommands
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IChatManager _chatManager = default!;

    public override string Command => "currency:transfer";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError("Usage: currency:transfer <player> <amount>");
            return;
        }

        var targetPlayerName = args[0];

        if (!int.TryParse(args[1], out var amount))
        {
            shell.WriteError("Amount must be a valid integer.");
            return;
        }

        if (amount <= 0)
        {
            shell.WriteError("Amount must be positive.");
            return;
        }

        // Get the sender (the player executing the command)
        var senderSession = shell.Player as ICommonSession;
        if (senderSession == null)
        {
            shell.WriteError("This command can only be used by players.");
            return;
        }

        // Find the target player
        ICommonSession? targetSession = null;
        foreach (var session in _playerManager.Sessions)
        {
            if (session.Name.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                targetSession = session;
                break;
            }
        }

        if (targetSession == null)
        {
            shell.WriteError($"Player '{targetPlayerName}' not found or not online.");
            return;
        }

        // Prevent self-transfer
        if (senderSession.UserId == targetSession.UserId)
        {
            shell.WriteError("You cannot transfer currency to yourself.");
            return;
        }

        if (_entities.System<BankSystem>().TryTransfer(senderSession, targetSession, amount, out var transfer))
        {
            var newSenderBalance = transfer.SenderBalance;
            var newTargetBalance = transfer.RecipientBalance;

            // Notify both players
            shell.WriteLine(Loc.GetString("bank-transfer-sent", ("amount", amount), ("player", targetPlayerName), ("balance", newSenderBalance)));

            // Notify the target player via chat
            var notificationMessage = Loc.GetString("bank-transfer-received", ("amount", amount), ("player", senderSession.Name), ("balance", newTargetBalance));
            _chatManager.ChatMessageToOne(
                ChatChannel.Notifications,
                notificationMessage,
                notificationMessage,
                EntityUid.Invalid,
                false,
                targetSession.Channel);
        }
        else
        {
            shell.WriteError(Loc.GetString("bank-transfer-rejected"));
        }
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        switch (args.Length)
        {
            case 1:
                var playerNames = _playerManager.Sessions.Select(s => s.Name).ToArray();
                return CompletionResult.FromOptions(playerNames);
            case 2:
                return CompletionResult.FromHint("Amount");
            default:
                return CompletionResult.Empty;
        }
    }
}
