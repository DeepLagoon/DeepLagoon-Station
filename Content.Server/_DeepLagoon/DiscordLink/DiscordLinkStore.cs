using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.DiscordLink;

/// <summary>
/// A dedicated durable identity database in the server's data directory.
/// All operations are serialized on the game thread. Raw codes are never stored.
/// </summary>
public sealed class DiscordLinkStore : IDisposable
{
    public static string ResolvePath(string root, string relative)
    {
        // A server-owned setting allows a dedicated directory with narrowly
        // inherited read ACLs for Identity, including recreated WAL/SHM files.
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(part => part is ".." or "." or ""))
            throw new ArgumentException("Discord link database must be relative to the server data directory.", nameof(relative));
        return Path.Combine(Path.GetFullPath(root), relative);
    }

    private readonly SqliteConnection _db;
    private readonly Func<long> _now;
    private readonly Dictionary<Guid, (long BoostUntil, int Tier, long TierUntil)> _supporterCache = new();

    public DiscordLinkStore(string path, Func<long>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS discord_api_cooldowns (bucket TEXT PRIMARY KEY, ready_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_staff_grants (ss14_uid TEXT PRIMARY KEY, kind TEXT NOT NULL, rank_id INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_spark (discord_id TEXT PRIMARY KEY, expires_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_boosty (discord_id TEXT PRIMARY KEY, tier INTEGER NOT NULL, expires_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_supporter_palette (id INTEGER PRIMARY KEY CHECK(id=1), boost INTEGER NOT NULL, tier1 INTEGER NOT NULL, tier2 INTEGER NOT NULL, tier3 INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_boosts (discord_id TEXT PRIMARY KEY, expires_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_links (
                discord_id TEXT PRIMARY KEY, ss14_uid TEXT NOT NULL UNIQUE,
                username TEXT NOT NULL, linked_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_launcher_enrollments (discord_id TEXT PRIMARY KEY, ss14_uid TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS discord_account_aliases (
                official_uid TEXT PRIMARY KEY, canonical_uid TEXT NOT NULL, discord_id TEXT NOT NULL,
                username TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_account_merge_codes (
                code_hash TEXT PRIMARY KEY, ss14_uid TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_account_merge_pending (
                code_hash TEXT PRIMARY KEY, discord_id TEXT NOT NULL, official_uid TEXT NOT NULL,
                canonical_uid TEXT NOT NULL, username TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_link_history (discord_id TEXT PRIMARY KEY, ss14_uid TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_link_revisions (ss14_uid TEXT PRIMARY KEY, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_link_codes (
                ss14_uid TEXT PRIMARY KEY, code_hash TEXT NOT NULL UNIQUE,
                username TEXT NOT NULL, expires_at INTEGER NOT NULL, issued_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS discord_link_attempts (
                discord_id TEXT PRIMARY KEY, window_start INTEGER NOT NULL, attempts INTEGER NOT NULL);
            """);
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    public bool ReserveApiCooldown(string bucket, int seconds)
    {
        using var command = Command("""
            INSERT INTO discord_api_cooldowns VALUES($bucket,$ready)
            ON CONFLICT(bucket) DO UPDATE SET ready_at=excluded.ready_at WHERE ready_at <= $now
            RETURNING ready_at
            """, ("$bucket", bucket), ("$ready", _now() + seconds), ("$now", _now()));
        return command.ExecuteScalar() != null;
    }

    public (string Kind, int RankId)? StaffGrant(Guid uid)
    {
        using var command = Command("SELECT kind,rank_id FROM discord_staff_grants WHERE ss14_uid=$uid", ("$uid", uid.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt32(1)) : null;
    }

    public void SetStaffGrant(Guid uid, string kind, int rankId)
        => Execute("INSERT INTO discord_staff_grants VALUES($uid,$kind,$rank) ON CONFLICT(ss14_uid) DO UPDATE SET kind=excluded.kind,rank_id=excluded.rank_id",
            ("$uid", uid.ToString()), ("$kind", kind), ("$rank", rankId));

    public void RemoveStaffGrant(Guid uid)
        => Execute("DELETE FROM discord_staff_grants WHERE ss14_uid=$uid", ("$uid", uid.ToString()));

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
        // Every entitlement/link mutation passes through Execute (link transactions bump revision).
        // Cache absolute deadlines, never a remaining duration: expiry needs no database query.
        _supporterCache.Clear();
    }

    public Link? FindDiscord(string discordId)
    {
        using var command = Command("SELECT ss14_uid, username FROM discord_links WHERE discord_id=$id", ("$id", discordId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Link(Guid.Parse(reader.GetString(0)), reader.GetString(1)) : null;
    }

    // A short renewable lease prevents permanent privileges after bot outages.
    public void SetBoost(string discordId, bool active)
    {
        if (active)
            Execute("INSERT INTO discord_boosts VALUES($id,$expires) ON CONFLICT(discord_id) DO UPDATE SET expires_at=excluded.expires_at",
                ("$id", discordId), ("$expires", _now() + 900));
        else
            Execute("DELETE FROM discord_boosts WHERE discord_id=$id", ("$id", discordId));
    }

    public int BoostRemaining(Guid uid)
    {
        return (int) Math.Clamp(Supporter(uid).BoostUntil - _now(), 0, 900);
    }

    private (long BoostUntil, int Tier, long TierUntil) Supporter(Guid uid)
    {
        if (_supporterCache.TryGetValue(uid, out var cached)) return cached;
        using var command = Command("""
            SELECT COALESCE(b.expires_at,0),COALESCE(t.tier,0),COALESCE(t.expires_at,0)
            FROM discord_links l LEFT JOIN discord_boosts b ON b.discord_id=l.discord_id
            LEFT JOIN discord_boosty t ON t.discord_id=l.discord_id WHERE l.ss14_uid=$uid
            """, ("$uid", uid.ToString()));
        using var reader = command.ExecuteReader();
        var status = reader.Read() ? (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2)) : (0L, 0, 0L);
        // Bound negative lookups too; disconnected IDs must not retain memory indefinitely.
        if (_supporterCache.Count >= 4096) _supporterCache.Clear();
        return _supporterCache[uid] = status;
    }

    public bool HasSpark(Guid uid)
    {
        using var command = Command("SELECT s.expires_at FROM discord_spark s JOIN discord_links l ON l.discord_id=s.discord_id WHERE l.ss14_uid=$uid", ("$uid", uid.ToString()));
        return command.ExecuteScalar() is long until && until > _now();
    }
    public void SetSupporter(string discordId, bool boostActive, int tier, int[] colors, bool spark = false)
    {
        if (tier is < 0 or > 3 || colors.Length != 4 || colors.Any(c => c is < 0 or > 0xFFFFFF))
            throw new ArgumentException("Invalid supporter state");
        using var transaction = _db.BeginTransaction();
        SetBoost(discordId, boostActive);
        if (spark) Execute("INSERT INTO discord_spark VALUES($id,$until) ON CONFLICT(discord_id) DO UPDATE SET expires_at=excluded.expires_at", ("$id", discordId), ("$until", _now() + 900));
        else Execute("DELETE FROM discord_spark WHERE discord_id=$id", ("$id", discordId));
        if (tier > 0)
            Execute("INSERT INTO discord_boosty VALUES($id,$tier,$expires) ON CONFLICT(discord_id) DO UPDATE SET tier=excluded.tier,expires_at=excluded.expires_at",
                ("$id", discordId), ("$tier", tier), ("$expires", _now() + 900));
        else
            Execute("DELETE FROM discord_boosty WHERE discord_id=$id", ("$id", discordId));
        Execute("INSERT INTO discord_supporter_palette VALUES(1,$boost,$tier1,$tier2,$tier3) ON CONFLICT(id) DO UPDATE SET boost=excluded.boost,tier1=excluded.tier1,tier2=excluded.tier2,tier3=excluded.tier3",
            ("$boost", colors[0]), ("$tier1", colors[1]), ("$tier2", colors[2]), ("$tier3", colors[3]));
        transaction.Commit();
    }

    public (int Tier, int Remaining) BoostyStatus(Guid uid)
    {
        var status = Supporter(uid);
        var remaining = (int) Math.Clamp(status.TierUntil - _now(), 0, 900);
        return remaining > 0 ? (status.Tier, remaining) : (0, 0);
    }

    public int[]? SupporterColors()
    {
        using var command = Command("SELECT boost,tier1,tier2,tier3 FROM discord_supporter_palette WHERE id=1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? new[] { reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3) } : null;
    }

    /// <summary>Trusted OAuth backend only. Never grants whitelist or replaces an existing UID.</summary>
    public Link EnrollLauncher(string discordId, string? discordUsername = null)
    {
        var existing = FindDiscord(discordId);
        if (existing != null) return existing;
        using var transaction = _db.BeginTransaction();
        using (var check = Command("SELECT 1 FROM discord_launcher_enrollments WHERE discord_id=$id UNION ALL SELECT 1 FROM discord_link_history WHERE discord_id=$id", ("$id", discordId)))
        {
            check.Transaction = transaction;
            if (check.ExecuteScalar() != null) throw new LinkException("enrollment_revoked");
        }
        var uid = Guid.NewGuid();
        var username = "Lagoon_" + discordId; // Compatibility with older OAuth backends.
        if (!string.IsNullOrWhiteSpace(discordUsername))
        {
            var nickname = new string(discordUsername.Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.').Take(30).ToArray());
            if (nickname.Length == 0) nickname = "player";
            for (var attempt = 0; ; attempt++)
            {
                var suffix = attempt == 0 ? "" : "_" + (attempt + 1);
                username = "@" + nickname[..Math.Min(nickname.Length, 30 - suffix.Length)] + suffix;
                using var check = Command("SELECT 1 FROM discord_links WHERE username=$name COLLATE NOCASE", ("$name", username));
                check.Transaction = transaction;
                if (check.ExecuteScalar() == null) break;
            }
        }
        using (var insert = Command("INSERT INTO discord_links VALUES($id,$uid,$name,$now)", ("$id", discordId), ("$uid", uid.ToString()), ("$name", username), ("$now", _now())))
        {
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        using (var record = Command("INSERT INTO discord_launcher_enrollments VALUES($id,$uid)", ("$id", discordId), ("$uid", uid.ToString())))
        {
            record.Transaction = transaction;
            record.ExecuteNonQuery();
        }
        BumpRevision(uid);
        transaction.Commit();
        return new Link(uid, username);
    }

    public void Unlink(string discordId, Guid uid)
    {
        if (MergePending(uid)) throw new LinkException("merge_pending");
        using var transaction = _db.BeginTransaction();
        using (var history = Command("INSERT OR IGNORE INTO discord_link_history VALUES($id,$uid)", ("$id", discordId), ("$uid", uid.ToString())))
        {
            history.Transaction = transaction;
            history.ExecuteNonQuery();
        }
        using (var record = Command("INSERT OR IGNORE INTO discord_launcher_enrollments VALUES($id,$uid)", ("$id", discordId), ("$uid", uid.ToString())))
        {
            record.Transaction = transaction;
            record.ExecuteNonQuery();
        }
        using (var command = Command("DELETE FROM discord_links WHERE discord_id=$id AND ss14_uid=$uid", ("$id", discordId), ("$uid", uid.ToString())))
        {
            command.Transaction = transaction;
            if (command.ExecuteNonQuery() != 1)
                throw new LinkException("not_linked");
        }
        using (var command = Command("DELETE FROM discord_link_codes WHERE ss14_uid=$uid", ("$uid", uid.ToString())))
        {
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        BumpRevision(uid);
        transaction.Commit();
    }

    public void AssertCurrent(string discordId, Guid uid, long linkedAt, long? revision = null)
    {
        using var command = Command("SELECT 1 FROM discord_links WHERE discord_id=$id AND ss14_uid=$uid AND linked_at=$at",
            ("$id", discordId), ("$uid", uid.ToString()), ("$at", linkedAt));
        if (command.ExecuteScalar() == null) throw new LinkException("stale_link");
        if (revision != null)
        {
            using var version = Command("SELECT revision FROM discord_link_revisions WHERE ss14_uid=$uid", ("$uid", uid.ToString()));
            if (Convert.ToInt64(version.ExecuteScalar() ?? 0L) != revision.Value) throw new LinkException("stale_link");
        }
    }

    public Link ReassignDiscord(string source, string target, Guid expectedUid, long expectedLinkedAt, long? revision = null)
    {
        if (MergePending(expectedUid)) throw new LinkException("merge_pending");
        using var transaction = _db.BeginTransaction();
        AssertCurrent(source, expectedUid, expectedLinkedAt, revision);
        var link = FindDiscord(source)!;
        if (source == target) return link;
        if (FindDiscord(target) != null) throw new LinkException("already_linked");
        using (var prior = Command("SELECT ss14_uid FROM discord_link_history WHERE discord_id=$id UNION ALL SELECT ss14_uid FROM discord_launcher_enrollments WHERE discord_id=$id", ("$id", target)))
        {
            prior.Transaction = transaction;
            using var reader = prior.ExecuteReader();
            while (reader.Read())
                if (Guid.Parse(reader.GetString(0)) != expectedUid) throw new LinkException("identity_conflict");
        }
        foreach (var id in new[] { source, target })
        {
            using var history = Command("INSERT OR IGNORE INTO discord_link_history VALUES($id,$uid)", ("$id", id), ("$uid", expectedUid.ToString()));
            history.Transaction = transaction;
            history.ExecuteNonQuery();
        }
        using (var change = Command("UPDATE discord_links SET discord_id=$target,linked_at=$now WHERE discord_id=$source",
                   ("$target", target), ("$source", source), ("$now", Math.Max(_now(), expectedLinkedAt + 1))))
        {
            change.Transaction = transaction;
            change.ExecuteNonQuery();
        }
        using (var codes = Command("DELETE FROM discord_link_codes WHERE ss14_uid=$uid", ("$uid", expectedUid.ToString())))
        {
            codes.Transaction = transaction;
            codes.ExecuteNonQuery();
        }
        BumpRevision(expectedUid);
        transaction.Commit();
        return link;
    }

    public Link RestoreDiscord(string discordId, Guid uid, string username)
    {
        if (MergePending(uid)) throw new LinkException("merge_pending");
        using var transaction = _db.BeginTransaction();
        if (FindDiscord(discordId) != null || IsLinked(uid)) throw new LinkException("already_linked");
        var found = false;
        using (var prior = Command("SELECT ss14_uid FROM discord_link_history WHERE discord_id=$id UNION ALL SELECT ss14_uid FROM discord_launcher_enrollments WHERE discord_id=$id", ("$id", discordId)))
        {
            prior.Transaction = transaction;
            using var reader = prior.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.Parse(reader.GetString(0)) != uid) throw new LinkException("identity_conflict");
                found = true;
            }
        }
        if (!found) throw new LinkException("not_linked");
        using var insert = Command("INSERT INTO discord_links VALUES($id,$uid,$name,$at)",
            ("$id", discordId), ("$uid", uid.ToString()), ("$name", username), ("$at", _now()));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        BumpRevision(uid);
        transaction.Commit();
        return new Link(uid, username);
    }

    public bool IsLinked(Guid uid)
    {
        using var command = Command("SELECT 1 FROM discord_links WHERE ss14_uid=$uid", ("$uid", uid.ToString()));
        return command.ExecuteScalar() != null;
    }

    public string Issue(Guid uid, string username)
    {
        if (MergePending(uid)) throw new LinkException("merge_pending");
        if (IsLinked(uid))
            throw new LinkException("already_linked");
        var now = _now();
        using (var command = Command("SELECT issued_at FROM discord_link_codes WHERE ss14_uid=$uid", ("$uid", uid.ToString())))
        {
            if (command.ExecuteScalar() is long issued && now - issued < 30)
                throw new LinkException("cooldown");
        }
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        Execute("DELETE FROM discord_link_codes WHERE expires_at <= $now", ("$now", now));
        Execute("""
            INSERT INTO discord_link_codes VALUES($uid,$hash,$name,$expires,$now)
            ON CONFLICT(ss14_uid) DO UPDATE SET code_hash=excluded.code_hash,
              username=excluded.username,expires_at=excluded.expires_at,issued_at=excluded.issued_at
            """, ("$uid", uid.ToString()), ("$hash", Hash(code)), ("$name", username), ("$expires", now + 600), ("$now", now));
        return code;
    }

    public Link Consume(string discordId, string code)
    {
        if (FindDiscord(discordId) != null)
            throw new LinkException("already_linked");
        var now = _now();
        // Count failed attempts durably so restarting the game does not reset the limit.
        Execute("DELETE FROM discord_link_attempts WHERE window_start <= $cutoff", ("$cutoff", now - 600));
        Execute("""
            INSERT INTO discord_link_attempts VALUES($id,$now,1)
            ON CONFLICT(discord_id) DO UPDATE SET attempts=attempts+1
            """, ("$id", discordId), ("$now", now));
        using (var command = Command("SELECT attempts FROM discord_link_attempts WHERE discord_id=$id", ("$id", discordId)))
        {
            if ((long)command.ExecuteScalar()! > 10)
                throw new LinkException("rate_limited");
        }
        using var transaction = _db.BeginTransaction();
        Link link;
        using (var command = Command("SELECT ss14_uid, username FROM discord_link_codes WHERE code_hash=$hash AND expires_at>$now",
                   ("$hash", Hash(code)), ("$now", now)))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new LinkException("invalid_code");
            link = new Link(Guid.Parse(reader.GetString(0)), reader.GetString(1));
        }
        if (MergePending(link.Uid)) throw new LinkException("merge_pending");
        using (var history = Command("SELECT ss14_uid FROM discord_link_history WHERE discord_id=$id UNION ALL SELECT ss14_uid FROM discord_launcher_enrollments WHERE discord_id=$id", ("$id", discordId)))
        {
            history.Transaction = transaction;
            using var reader = history.ExecuteReader();
            while (reader.Read())
                if (Guid.Parse(reader.GetString(0)) != link.Uid) throw new LinkException("identity_conflict");
        }
        using (var command = Command("INSERT INTO discord_links VALUES($id,$uid,$name,$now)",
                   ("$id", discordId), ("$uid", link.Uid.ToString()), ("$name", link.Username), ("$now", now)))
        {
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        using (var command = Command("DELETE FROM discord_link_codes WHERE ss14_uid=$uid", ("$uid", link.Uid.ToString())))
        {
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        BumpRevision(link.Uid);
        transaction.Commit();
        return link;
    }

    private void BumpRevision(Guid uid) => Execute("INSERT INTO discord_link_revisions VALUES($uid,1) ON CONFLICT(ss14_uid) DO UPDATE SET revision=revision+1", ("$uid", uid.ToString()));

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    public string IssueMerge(Guid uid, string username)
    {
        var code = Issue(uid, username);
        Execute("DELETE FROM discord_account_merge_codes WHERE ss14_uid=$uid", ("$uid", uid.ToString()));
        Execute("INSERT INTO discord_account_merge_codes VALUES($hash,$uid)", ("$hash", Hash(code)), ("$uid", uid.ToString()));
        return code;
    }

    public bool IsMergeCode(string code)
    {
        using var query = Command("SELECT 1 FROM discord_account_merge_codes WHERE code_hash=$hash UNION ALL SELECT 1 FROM discord_account_merge_pending WHERE code_hash=$hash", ("$hash", Hash(code)));
        return query.ExecuteScalar() != null;
    }

    public sealed record MergePlan(Guid OfficialUid, Guid CanonicalUid, string Username);

    public bool MergePending(Guid uid)
    {
        using var query = Command("SELECT 1 FROM discord_account_merge_pending p WHERE (p.official_uid=$uid OR p.canonical_uid=$uid) AND NOT EXISTS (SELECT 1 FROM discord_account_aliases a WHERE a.official_uid=p.official_uid AND a.canonical_uid=p.canonical_uid AND a.discord_id=p.discord_id)", ("$uid", uid.ToString()));
        return query.ExecuteScalar() != null;
    }

    public MergePlan PrepareMerge(string discordId, string code)
    {
        var canonical = FindDiscord(discordId) ?? throw new LinkException("no_existing_account");
        var hash = Hash(code);
        using (var pending = Command("SELECT official_uid,canonical_uid,username,discord_id FROM discord_account_merge_pending WHERE code_hash=$hash", ("$hash", hash)))
        {
            using var reader = pending.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(3) != discordId || Guid.Parse(reader.GetString(1)) != canonical.Uid)
                    throw new LinkException("identity_conflict");
                return new MergePlan(Guid.Parse(reader.GetString(0)), canonical.Uid, reader.GetString(2));
            }
        }
        using var query = Command("SELECT c.ss14_uid,c.username FROM discord_link_codes c JOIN discord_account_merge_codes m ON m.code_hash=c.code_hash WHERE c.code_hash=$hash AND c.expires_at>$now", ("$hash", hash), ("$now", _now()));
        Guid official;
        string username;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) throw new LinkException("invalid_code");
            official = Guid.Parse(reader.GetString(0));
            username = reader.GetString(1);
        }
        if (official == canonical.Uid || IsLinked(official)) throw new LinkException("already_linked");
        using (var owner = Command("SELECT 1 FROM discord_link_history WHERE ss14_uid=$uid AND discord_id<>$discord UNION ALL SELECT 1 FROM discord_launcher_enrollments WHERE ss14_uid=$uid AND discord_id<>$discord", ("$uid", official.ToString()), ("$discord", discordId)))
            if (owner.ExecuteScalar() != null) throw new LinkException("identity_conflict");
        using (var other = Command("SELECT 1 FROM discord_account_merge_pending WHERE official_uid IN ($a,$b) OR canonical_uid IN ($a,$b) UNION ALL SELECT 1 FROM discord_account_aliases WHERE official_uid=$a", ("$a", official.ToString()), ("$b", canonical.Uid.ToString())))
            if (other.ExecuteScalar() != null) throw new LinkException("merge_pending");
        Execute("INSERT INTO discord_account_merge_pending VALUES($hash,$discord,$official,$canonical,$name)", ("$hash", hash), ("$discord", discordId), ("$official", official.ToString()), ("$canonical", canonical.Uid.ToString()), ("$name", username));
        return new MergePlan(official, canonical.Uid, username);
    }

    public Link CompleteMerge(string discordId, string code, MergePlan plan)
    {
        if (PrepareMerge(discordId, code) != plan) throw new LinkException("identity_conflict");
        using var transaction = _db.BeginTransaction();
        Execute("INSERT INTO discord_account_aliases VALUES($official,$canonical,$discord,$name) ON CONFLICT(official_uid) DO UPDATE SET username=excluded.username", ("$official", plan.OfficialUid.ToString()), ("$canonical", plan.CanonicalUid.ToString()), ("$discord", discordId), ("$name", plan.Username));
        Execute("UPDATE discord_links SET username=$name WHERE discord_id=$discord", ("$name", plan.Username), ("$discord", discordId));
        Execute("DELETE FROM discord_link_codes WHERE ss14_uid=$uid", ("$uid", plan.OfficialUid.ToString()));
        // Keep the pending proof as a durable replay receipt. It is no longer a
        // login barrier once the alias exists; only the same Discord can retry.
        BumpRevision(plan.CanonicalUid);
        BumpRevision(plan.OfficialUid);
        transaction.Commit();
        return new Link(plan.CanonicalUid, plan.Username);
    }

    public bool MergeCompleted(string discordId, MergePlan plan)
    {
        using var query = Command("SELECT 1 FROM discord_account_aliases WHERE official_uid=$official AND canonical_uid=$canonical AND discord_id=$discord", ("$official", plan.OfficialUid.ToString()), ("$canonical", plan.CanonicalUid.ToString()), ("$discord", discordId));
        return query.ExecuteScalar() != null;
    }
    public void Dispose() => _db.Dispose();
    public sealed record Link(Guid Uid, string Username);
    public sealed class LinkException(string code) : Exception(code);
}
