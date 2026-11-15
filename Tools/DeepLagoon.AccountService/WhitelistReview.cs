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
    [property: JsonPropertyName("related_uids")] Guid[] RelatedUsers);

internal sealed class WhitelistReviewRequired(string code, WhitelistReview review) : Exception(code)
{
    public string Code { get; } = code;
    public WhitelistReview Review { get; } = review;
}

public sealed partial class AccountOperations
{
    // UUIDs are returned only to the authenticated ticket bot so it can look up its own
    // ticket/whitelist audit. The database never returns addresses, HWIDs or ban reasons.
    private async Task<WhitelistReview> CheckWhitelist(ServerDbContext db, string discord, Guid uid, Guid[]? knownUids, CancellationToken cancel)
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
                UNION SELECT user_id FROM (SELECT user_id FROM address_users ORDER BY user_id LIMIT 100) ip
            )
            SELECT EXISTS (SELECT 1 FROM target),
                   EXISTS (SELECT 1 FROM hardware_users),
                   EXISTS (SELECT 1 FROM address_users),
                   EXISTS (SELECT 1 FROM active_bans b
                           WHERE b.player_user_id IN ({{linked}}) OR b.player_user_id IN (SELECT user_id FROM hardware_users)
                           OR EXISTS (SELECT 1 FROM target_hardware h WHERE h.hwid=b.hwid AND h.hwid_type=b.hwid_type)),
                   EXISTS (SELECT 1 FROM active_bans b WHERE b.player_user_id IN (SELECT user_id FROM address_users)),
                   EXISTS (SELECT 1 FROM whitelist w WHERE w.user_id<>@uid
                           AND (w.user_id IN ({{linked}}) OR w.user_id IN (SELECT user_id FROM hardware_users)
                                OR w.user_id IN (SELECT user_id FROM address_users))),
                   h.user_id
            FROM (SELECT 1) flags LEFT JOIN historical_lookup h ON TRUE
            """;
        // The limit above only bounds the bot's historical audit lookup. Ban and current
        // whitelist checks use all matches, including addresses shared by many accounts.
        await using var rows = await command.ExecuteReaderAsync(cancel);
        await rows.ReadAsync(cancel);
        var findings = new WhitelistReview(rows.GetBoolean(0), discordHistory || relatedDiscordUsers.Length > 0,
            rows.GetBoolean(1), rows.GetBoolean(2), rows.GetBoolean(3), rows.GetBoolean(4), rows.GetBoolean(5), []);
        var related = relatedDiscordUsers.ToHashSet();
        do
        {
            if (!rows.IsDBNull(6))
                related.Add(rows.GetGuid(6));
        } while (await rows.ReadAsync(cancel));
        return findings with { RelatedUsers = related.ToArray() };
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
}
