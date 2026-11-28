using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server._NF.Bank;
using Content.Shared.Administration;
using Content.Shared.Preferences;
using Content.Shared._NF.Bank.Components;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._NF.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed partial class BankCommand : IConsoleCommand
{
    [Dependency] private IServerPreferencesManager _prefsManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IServerDbManager _dbManager = default!;
    [Dependency] private IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private IEntityManager _entityManager = default!;

    public string Command => "bank";

    public string Description => "Modifies a player's bank account balance.";

    public string Help => "bank <username/character> <amount>\n" +
                          "Adds or removes the specified amount from the player's bank account. " +
                          "Use positive values to add money, negative values to remove money. " +
                          "Works with online players, offline players in cache, and players in database.";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError("Wrong number of arguments.\nUsage: " + Help);
            return;
        }

        var target = args[0];
        if (!long.TryParse(args[1], out var amount) || amount == long.MinValue)
        {
            shell.WriteError("Amount must be a valid number.");
            return;
        }

        // First try online players by name
        var onlinePlayer = _playerManager.Sessions
            .FirstOrDefault(s => s.Name.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (onlinePlayer != null)
        {
            // Handle online player
            await HandleOnlinePlayer(shell, onlinePlayer, amount, target);
            return;
        }

        // If player name not found online, try by ID
        if (Guid.TryParse(target, out var userId))
        {
            onlinePlayer = _playerManager.Sessions.FirstOrDefault(s => s.UserId == userId);
            if (onlinePlayer != null)
            {
                await HandleOnlinePlayer(shell, onlinePlayer, amount, target);
                return;
            }
        }

        // If not online, check cached preferences for offline players
        if (TryGetOfflinePlayerData(target, out var offlineUserId, out var offlinePrefs, out var offlineProfile))
        {
            await HandleOfflinePlayer(shell, offlineUserId, offlinePrefs, offlineProfile, amount, target);
            return;
        }

        // If not in cache, try the database
        var record = await _dbManager.GetPlayerRecordByUserName(target);
        if (record != null)
        {
            var dbUserId = record.UserId;
            var dbPrefs = await _dbManager.GetPlayerPreferencesAsync(dbUserId, default);
            if (dbPrefs != null &&
                dbPrefs.SelectedCharacterIndex >= 0 &&
                dbPrefs.Characters.TryGetValue(dbPrefs.SelectedCharacterIndex, out var dbProfile))
            {
                if (dbProfile is HumanoidCharacterProfile dbHumanoid)
                {
                    await HandleOfflinePlayer(shell, dbUserId, dbPrefs, dbHumanoid, amount, target);
                    return;
                }
            }
        }

        shell.WriteError($"Unable to find player or character '{target}'.");
    }

    private async Task HandleOnlinePlayer(IConsoleShell shell, ICommonSession targetSession, long amount, string target)
    {
        var bank = _entitySystemManager.GetEntitySystem<BankSystem>();
        if (!_prefsManager.TryGetCachedPreferences(targetSession.UserId, out var prefs))
        {
            shell.WriteError($"Unable to retrieve preferences for player '{target}'.");
            return;
        }
        HumanoidCharacterProfile? profile;
        int? expectedProfile = null;
        if (bank.TryGetCharacter(targetSession, out _, out var current, out _))
        {
            profile = current;
            if (targetSession.AttachedEntity is { } body && _entityManager.TryGetComponent<BankCharacterComponent>(body, out var owner))
                expectedProfile = owner.Profile;
        }
        else if (_entitySystemManager.GetEntitySystem<Content.Server.Mind.MindSystem>()
                     .TryGetMind(targetSession.UserId, out var mind, out _) &&
                 _entityManager.TryGetComponent<BankCharacterComponent>(mind.Value, out var remembered) &&
                 remembered.User == targetSession.UserId.UserId)
        {
            // Observe/cryo must not redirect an administrator's command to a newly selected lobby character.
            profile = prefs.Characters.GetValueOrDefault(remembered.Slot) as HumanoidCharacterProfile;
            expectedProfile = remembered.Profile;
        }
        else profile = prefs.SelectedCharacter as HumanoidCharacterProfile;
        if (profile == null)
        {
            shell.WriteError($"Player '{target}' has an invalid character profile.");
            return;
        }
        await AdjustAccount(shell, targetSession.UserId, prefs, profile, amount, target, expectedProfile);
    }

    private Task HandleOfflinePlayer(IConsoleShell shell, NetUserId userId, PlayerPreferences prefs,
        HumanoidCharacterProfile profile, long amount, string target)
        => AdjustAccount(shell, userId, prefs, profile, amount, target);

    private async Task AdjustAccount(IConsoleShell shell, NetUserId userId, PlayerPreferences prefs,
        HumanoidCharacterProfile profile, long amount, string target, int? expectedProfile = null)
    {
        if (amount == 0)
        {
            shell.WriteLine($"Player '{target}' balance unchanged: {profile.BankBalance}");
            return;
        }
        var slot = prefs.IndexOfCharacter(profile);
        if (slot < 0)
        {
            shell.WriteError("The character profile changed. Retry the command.");
            return;
        }
        var bank = _entitySystemManager.GetEntitySystem<BankSystem>();
        if (!bank.BeginExchangePayment(userId))
        {
            shell.WriteError("Another bank operation is pending. Retry the command.");
            return;
        }
        var actor = shell.Player;
        var actorName = actor?.Name ?? "SERVER CONSOLE";
        try
        {
            var account = await _dbManager.ReadEconomyAccountAsync(userId.UserId, slot);
            if (account.Character != profile.Name || account.Bank != profile.BankBalance ||
                expectedProfile != null && account.Profile != expectedProfile)
                throw new InvalidOperationException("The character account changed. Refresh and retry the command.");
            var result = await _dbManager.AdjustBankByAdminAsync(userId,
                new(Guid.NewGuid(), slot, account.Profile, account.Bank, amount, actor?.UserId.UserId, actorName, target));
            // The audit/outbox and balance are committed together before reporting success.
            shell.WriteLine(amount > 0
                ? $"Added {amount} to player '{target}' ({account.Character}). New balance: {result.Balance}"
                : $"Removed {Math.Abs(amount)} from player '{target}' ({account.Character}). New balance: {result.Balance}");
            try
            {
                if (_playerManager.TryGetSessionById(userId, out _))
                    await bank.PublishExchangeBalance(userId, slot);
                else if (_prefsManager.TryGetCachedPreferences(userId, out var cached) &&
                         ReferenceEquals(cached.Characters.GetValueOrDefault(slot), profile))
                    await _prefsManager.SetProfile(userId, slot, profile.WithBankBalance(result.Balance), persist: false);
            }
            catch (Exception)
            {
                // Refresh failures must not make a committed grant appear unsuccessful and invite a duplicate grant.
                shell.WriteError("The balance change and audit were saved, but the displayed balance needs refreshing.");
            }
        }
        catch (Exception error)
        {
            shell.WriteError($"Bank adjustment rejected: {error.Message}");
        }
        finally { bank.EndExchangePayment(userId); }
    }

    private bool TryGetOfflinePlayerData(string username, out NetUserId userId, out PlayerPreferences prefs, out HumanoidCharacterProfile profile)
    {
        userId = default;
        prefs = null!;
        profile = null!;

        // Check all users in the preferences cache
        foreach (var playerData in _playerManager.GetAllPlayerData())
        {
            if (_prefsManager.TryGetCachedPreferences(playerData.UserId, out var cachedPrefs))
            {
                foreach (var (_, characterProfile) in cachedPrefs.Characters)
                {
                    if (characterProfile is HumanoidCharacterProfile humanoid &&
                        humanoid.Name.Equals(username, StringComparison.OrdinalIgnoreCase))
                    {
                        userId = playerData.UserId;
                        prefs = cachedPrefs;
                        profile = humanoid;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var options = new List<string>();

            // Add online players
            options.AddRange(_playerManager.Sessions.Select(s => s.Name));

            // Add players from cached preferences
            foreach (var playerData in _playerManager.GetAllPlayerData())
            {
                if (_prefsManager.TryGetCachedPreferences(playerData.UserId, out var prefs))
                {
                    foreach (var (_, profile) in prefs.Characters)
                    {
                        if (profile is HumanoidCharacterProfile humanoid)
                        {
                            options.Add(humanoid.Name);
                        }
                    }
                }
            }

            return CompletionResult.FromHintOptions(options.Distinct(), "<username/character>");
        }

        if (args.Length == 2)
        {
            // For the amount parameter, provide some common values as suggestions
            var amountOptions = new List<CompletionOption>
            {
                new CompletionOption("100", "Add 100 credits"),
                new CompletionOption("1000", "Add 1000 credits"),
                new CompletionOption("10000", "Add 10000 credits"),
                new CompletionOption("-100", "Remove 100 credits"),
                new CompletionOption("-1000", "Remove 1000 credits")
            };

            return CompletionResult.FromOptions(amountOptions);
        }

        return CompletionResult.Empty;
    }
}
