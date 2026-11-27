using System.Text.Json.Serialization;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DeepLagoon.AccountService;

internal sealed record WhitelistReview(
    [property: JsonPropertyName("has_connection")] bool HasConnection,
    [property: JsonPropertyName("discord_history")] bool DiscordHistory,
    [property: JsonPropertyName("hwid_match")] bool HardwareMatch,
    [property: JsonPropertyName("ip_match")] bool AddressMatch,
    [property: JsonPropertyName("active_ban")] bool ActiveBan,
    [property: JsonPropertyName("shared_ip_ban")] bool SharedAddressBan,
    [property: JsonPropertyName("related_whitelist")] bool RelatedWhitelist,
    [property: JsonPropertyName("related_uids")] Guid[] RelatedUsers)
{
    [JsonPropertyName("related_accounts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhitelistAccount[]? Accounts { get; init; }

    // Lightweight per-account provenance is always present, including grant responses.
    // The bot must not infer a strong match for B from a hardware match for A.
    [JsonPropertyName("related_matches")]
    public WhitelistMatch[] Matches { get; init; } = [];
}

internal sealed record WhitelistMatch(
    [property: JsonPropertyName("uid")] Guid Uid,
    [property: JsonPropertyName("ip_match")] bool AddressMatch,
    [property: JsonPropertyName("hwid_match")] bool HardwareMatch,
    [property: JsonPropertyName("discord_history")] bool DiscordHistory,
    [property: JsonPropertyName("whitelisted")] bool Whitelisted);

internal sealed record WhitelistAccount(
    [property: JsonPropertyName("uid")] Guid Uid,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("discord_ids")] string[] DiscordIds,
    [property: JsonPropertyName("ip_match")] bool AddressMatch,
    [property: JsonPropertyName("hwid_match")] bool HardwareMatch,
    [property: JsonPropertyName("discord_history")] bool DiscordHistory);

internal sealed class WhitelistReviewRequired(string code, WhitelistReview review) : Exception(code)
{
    public string Code { get; } = code;
    public WhitelistReview Review { get; } = review;
}

public sealed partial class AccountOperations
{
    // UUIDs are returned only to the authenticated ticket bot so it can look up its own
    // ticket/whitelist audit. The database never returns addresses, HWIDs or ban reasons.
    private async Task<WhitelistReview> CheckWhitelist(ServerDbContext db, string discord, Guid uid, Guid[]? knownUids,
        bool includeAccounts, CancellationToken cancel)
    {
        var (discordHistory, discordUsers) = GetWhitelistLinkHistory(discord, uid);
        var relatedDiscordUsers = discordUsers.Concat(knownUids ?? []).Where(id => id != uid).Distinct().ToArray();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = db.Database.GetCommandTimeout() ?? 8;
        string Bind(string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
            return "@" + name;
        }

        Bind("uid", uid);
        Bind("now", DateTime.UtcNow);
        // Only generated parameter names are interpolated. All accounts are bound as UUIDs.
        var linked = string.Join(", ", relatedDiscordUsers.Select((id, index) => Bind("related" + index, id)).Prepend("@uid"));
        command.CommandText = $$"""
            WITH target AS (
                SELECT address, hwid, hwid_type FROM connection_log WHERE user_id=@uid
            ), target_hardware AS (
                SELECT DISTINCT hwid, hwid_type FROM target WHERE hwid IS NOT NULL AND length(hwid)>0
            ), hardware_users AS (
                SELECT DISTINCT c.user_id FROM connection_log c
                JOIN target_hardware h ON h.hwid=c.hwid AND h.hwid_type=c.hwid_type
                WHERE c.user_id<>@uid
            ), address_users AS (
                SELECT DISTINCT c.user_id FROM connection_log c
                JOIN (SELECT DISTINCT address FROM target) t ON t.address=c.address
                WHERE c.user_id<>@uid
            ), active_bans AS (
                SELECT b.player_user_id, b.hwid, b.hwid_type FROM server_ban b
                WHERE (b.expiration_time IS NULL OR b.expiration_time>@now)
                AND NOT EXISTS (SELECT 1 FROM server_unban u WHERE u.ban_id=b.server_ban_id)
            ), historical_lookup AS (
                SELECT user_id FROM (SELECT user_id FROM hardware_users ORDER BY user_id LIMIT 100) hw
                UNION SELECT user_id FROM hardware_users WHERE user_id IN ({{linked}})
                UNION SELECT user_id FROM (SELECT hw.user_id FROM hardware_users hw
                    JOIN whitelist w ON w.user_id=hw.user_id ORDER BY hw.user_id LIMIT 1) whitelisted_hw
            )
            SELECT EXISTS (SELECT 1 FROM target),
                   EXISTS (SELECT 1 FROM hardware_users),
                   EXISTS (SELECT 1 FROM address_users),
                   EXISTS (SELECT 1 FROM active_bans b
                           WHERE b.player_user_id IN ({{linked}}) OR b.player_user_id IN (SELECT user_id FROM hardware_users)
                           OR EXISTS (SELECT 1 FROM target_hardware h WHERE h.hwid=b.hwid AND h.hwid_type=b.hwid_type)),
                   EXISTS (SELECT 1 FROM active_bans b WHERE b.player_user_id IN (SELECT user_id FROM address_users)),
                   EXISTS (SELECT 1 FROM whitelist w WHERE w.user_id<>@uid
                           AND (w.user_id IN ({{linked}}) OR w.user_id IN (SELECT user_id FROM hardware_users))),
                   h.user_id,
                   EXISTS (SELECT 1 FROM address_users ip WHERE ip.user_id=h.user_id),
                   EXISTS (SELECT 1 FROM hardware_users hw WHERE hw.user_id=h.user_id)
            FROM (SELECT 1) flags LEFT JOIN historical_lookup h ON TRUE
            """;
        // IP is informational only. Related accounts, whitelist history and blocking
        // bans use HWID or Discord history; a shared address does not link accounts.
        var matches = new Dictionary<Guid, (bool Address, bool Hardware)>();
        WhitelistReview findings;
        await using (var rows = await command.ExecuteReaderAsync(cancel))
        {
            await rows.ReadAsync(cancel);
            findings = new WhitelistReview(rows.GetBoolean(0), discordHistory || relatedDiscordUsers.Length > 0,
                rows.GetBoolean(1), rows.GetBoolean(2), rows.GetBoolean(3), rows.GetBoolean(4), rows.GetBoolean(5), []);
            do
            {
                if (!rows.IsDBNull(6))
                    matches.Add(rows.GetGuid(6), (rows.GetBoolean(7), rows.GetBoolean(8)));
            } while (await rows.ReadAsync(cancel));
        }
        var related = relatedDiscordUsers.Concat(matches.Keys).Distinct().ToArray();
        var whitelisted = await db.Whitelist.Where(entry => related.Contains(entry.UserId))
            .Select(entry => entry.UserId).ToHashSetAsync(cancel);
        findings = findings with
        {
            RelatedUsers = related,
            Matches = related.Select(id =>
            {
                var (address, hardware) = matches.GetValueOrDefault(id);
                return new WhitelistMatch(id, address, hardware, relatedDiscordUsers.Contains(id), whitelisted.Contains(id));
            }).ToArray()
        };
        if (!includeAccounts)
            return findings;
        var history = relatedDiscordUsers.ToHashSet();
        if (discordHistory)
            history.Add(uid);
        var accounts = await GetWhitelistAccounts(db, related.Concat(history).Distinct().ToArray(), cancel);
        return findings with { Accounts = accounts.Select(account =>
        {
            var (address, hardware) = matches.GetValueOrDefault(account.Uid);
            return account with { AddressMatch = address, HardwareMatch = hardware, DiscordHistory = history.Contains(account.Uid) };
        }).ToArray() };
    }

    private (bool Changed, Guid[] Related) GetWhitelistLinkHistory(string discord, Guid uid)
    {
        using var db = OpenLinks();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT ss14_uid,discord_id FROM discord_link_history WHERE discord_id=$discord OR ss14_uid=$uid
            UNION ALL SELECT ss14_uid,discord_id FROM discord_launcher_enrollments WHERE discord_id=$discord OR ss14_uid=$uid
            UNION ALL SELECT official_uid,discord_id FROM discord_account_aliases WHERE discord_id=$discord OR canonical_uid=$uid
            UNION ALL SELECT canonical_uid,discord_id FROM discord_account_aliases WHERE discord_id=$discord OR official_uid=$uid
            """;
        command.Parameters.AddWithValue("$discord", discord);
        command.Parameters.AddWithValue("$uid", uid.ToString());
        using var rows = command.ExecuteReader();
        var changed = false;
        var related = new HashSet<Guid>();
        while (rows.Read())
        {
            var relatedUid = Guid.Parse(rows.GetString(0));
            changed |= relatedUid != uid || rows.GetString(1) != discord;
            if (relatedUid != uid)
                related.Add(relatedUid);
        }

        return (changed, related.ToArray());
    }

    private async Task<WhitelistAccount[]> GetWhitelistAccounts(ServerDbContext db, Guid[] ids, CancellationToken cancel)
    {
        if (ids.Length == 0)
            return [];
        var usernames = await db.Player.Where(p => ids.Contains(p.UserId))
            .Select(p => new { p.UserId, p.LastSeenUserName })
            .ToDictionaryAsync(p => p.UserId, p => p.LastSeenUserName, cancel);
        var discordIds = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal));
        using var links = OpenLinks();
        using var command = links.CreateCommand();
        var parameters = ids.Select((id, index) =>
        {
            var name = "$uid" + index;
            command.Parameters.AddWithValue(name, id.ToString());
            return name;
        });
        var selected = string.Join(", ", parameters);
        command.CommandText = $$"""
            SELECT ss14_uid, discord_id, username FROM discord_links WHERE ss14_uid IN ({{selected}})
            UNION ALL SELECT ss14_uid, discord_id, NULL FROM discord_link_history WHERE ss14_uid IN ({{selected}})
            UNION ALL SELECT ss14_uid, discord_id, NULL FROM discord_launcher_enrollments WHERE ss14_uid IN ({{selected}})
            UNION ALL SELECT official_uid, discord_id, username FROM discord_account_aliases WHERE official_uid IN ({{selected}})
            UNION ALL SELECT canonical_uid, discord_id, username FROM discord_account_aliases WHERE canonical_uid IN ({{selected}})
            """;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var id = Guid.Parse(rows.GetString(0));
            var discord = rows.GetString(1);
            if (DiscordId(discord))
                discordIds[id].Add(discord);
            if (!rows.IsDBNull(2))
                usernames.TryAdd(id, rows.GetString(2));
        }
        return ids.Select(id => new WhitelistAccount(id, usernames.GetValueOrDefault(id),
                discordIds[id].Order(StringComparer.Ordinal).ToArray(), false, false, false))
            .OrderBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(account => account.Uid).ToArray();
    }
}
