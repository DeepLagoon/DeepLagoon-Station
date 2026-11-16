using System.Linq;
using Content.Shared._DeepLagoon.Ahelp;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.Ahelp;

public sealed class AhelpStore : IDisposable
{
    public sealed record Entry(long Id, AhelpTicket Ticket, string Kind, AhelpLine Line, string Origin);
    public sealed record Attachment(string Name, string Mime, byte[] Bytes);
    public sealed record Result(string Error, Entry? Event = null, bool Applied = false);

    private const string TicketColumns = "t.id,t.number,t.player,t.player_name,t.assignee,t.assignee_name,t.status,t.created";
    private const string EventColumns = "e.id,e.kind,e.author,e.author_name,e.text,e.staff,e.created,e.file,e.mime,e.origin";
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();
    private SqliteTransaction? _transaction;

    public AhelpStore(string path)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var schema = Command("""
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS tickets(
                number INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, player TEXT NOT NULL,
                player_name TEXT NOT NULL, assignee TEXT, assignee_name TEXT, status INTEGER NOT NULL,
                created INTEGER NOT NULL, queued INTEGER NOT NULL, relay INTEGER NOT NULL DEFAULT 0,
                outcome INTEGER, reason TEXT, terminal INTEGER);
            CREATE UNIQUE INDEX IF NOT EXISTS active_ahelp ON tickets(player) WHERE status < 3;
            CREATE TABLE IF NOT EXISTS events(
                id INTEGER PRIMARY KEY AUTOINCREMENT, ticket TEXT NOT NULL REFERENCES tickets(id), kind TEXT NOT NULL,
                author TEXT NOT NULL, author_name TEXT NOT NULL, text TEXT NOT NULL, staff INTEGER NOT NULL,
                created INTEGER NOT NULL, origin TEXT NOT NULL UNIQUE, file TEXT, mime TEXT, data BLOB,
                rewarded INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ticket_events ON events(ticket,id);
            CREATE TABLE IF NOT EXISTS reads(
                ticket TEXT NOT NULL REFERENCES tickets(id), reader TEXT NOT NULL, seen INTEGER NOT NULL,
                PRIMARY KEY(ticket,reader));
            CREATE TABLE IF NOT EXISTS relay_delivery(
                event INTEGER PRIMARY KEY REFERENCES events(id), done INTEGER NOT NULL, retry INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS relay_media(
                event INTEGER PRIMARY KEY REFERENCES events(id), retry INTEGER NOT NULL DEFAULT 0);
            """);
        schema.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Key, object? Value)[] values)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        foreach (var (key, value) in values)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }

    private int Execute(string sql, params (string, object?)[] values)
    {
        using var command = Command(sql, values);
        return command.ExecuteNonQuery();
    }

    private T Write<T>(Func<T> operation)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            _transaction = transaction;
            try
            {
                var result = operation();
                transaction.Commit();
                return result;
            }
            finally { _transaction = null; }
        }
    }

    private static AhelpTicket Ticket(SqliteDataReader row, int index = 0)
        => new(Guid.Parse(row.GetString(index)), row.GetInt64(index + 1), Guid.Parse(row.GetString(index + 2)),
            row.GetString(index + 3), row.IsDBNull(index + 4) ? null : Guid.Parse(row.GetString(index + 4)),
            row.IsDBNull(index + 5) ? null : row.GetString(index + 5), (AhelpStatus) row.GetInt32(index + 6),
            row.GetInt64(index + 7));

    private static Entry Event(SqliteDataReader row)
        => new(row.GetInt64(0), Ticket(row, 10), row.GetString(1),
            new AhelpLine(row.GetInt64(0), Guid.Parse(row.GetString(2)), row.GetString(3), row.GetString(4),
                row.GetBoolean(5), row.GetInt64(6), row.IsDBNull(7) ? null : row.GetString(7),
                row.IsDBNull(8) ? null : row.GetString(8)), row.GetString(9));

    private Entry? FindEvent(string origin)
    {
        using var cmd = Command($"SELECT {EventColumns},{TicketColumns} FROM events e JOIN tickets t ON e.ticket=t.id WHERE e.origin=$origin",
            ("$origin", origin));
        using var rows = cmd.ExecuteReader();
        return rows.Read() ? Event(rows) : null;
    }

    private AhelpTicket? Find(Guid id, Guid player = default)
    {
        using var cmd = id == Guid.Empty
            ? Command($"SELECT {TicketColumns} FROM tickets t WHERE t.player=$player AND t.status<3", ("$player", player.ToString()))
            : Command($"SELECT {TicketColumns} FROM tickets t WHERE t.id=$id", ("$id", id.ToString()));
        using var rows = cmd.ExecuteReader();
        return rows.Read() ? Ticket(rows) : null;
    }

    public AhelpTicket? Get(Guid id)
    {
        lock (_lock) return Find(id);
    }

    private Result Add(AhelpTicket ticket, string kind, Guid actor, string name, string text, bool staff,
        string origin, long now, Attachment? file = null)
    {
        using var cmd = Command("""
            INSERT INTO events(ticket,kind,author,author_name,text,staff,origin,created,file,mime,data)
            VALUES ($ticket,$kind,$actor,$name,$text,$staff,$origin,$at,$file,$mime,$data) RETURNING id
            """, ("$ticket", ticket.Id.ToString()), ("$kind", kind), ("$actor", actor.ToString()), ("$name", name),
            ("$text", text), ("$staff", staff), ("$origin", origin), ("$at", now), ("$file", file?.Name),
            ("$mime", file?.Mime), ("$data", file?.Bytes));
        var id = (long) cmd.ExecuteScalar()!;
        return new Result("", new Entry(id, ticket, kind, new AhelpLine(id, actor, name, text, staff, now, file?.Name, file?.Mime), origin), true);
    }

    public Result Send(Guid id, Guid player, string playerName, Guid actor, string name, bool admin, bool staff,
        string text, string origin, long now, Attachment? file = null, bool drain = false)
        => Write(() =>
        {
            if (FindEvent(origin) is { } prior)
                return new Result("", prior);
            var ticket = Find(id, player);
            if (ticket == null)
            {
                if (id != Guid.Empty || player == Guid.Empty || staff || player != actor && !admin)
                    return new Result("forbidden");
                id = Guid.NewGuid();
                var assignee = admin && player != actor ? actor : (Guid?) null;
                Execute("""
                    INSERT INTO tickets(id,player,player_name,assignee,assignee_name,status,created,queued)
                    VALUES ($id,$player,$playerName,$assignee,$assigneeName,$status,$now,$now)
                    """, ("$id", id.ToString()), ("$player", player.ToString()), ("$playerName", playerName),
                    ("$assignee", assignee?.ToString()), ("$assigneeName", assignee == null ? null : name),
                    ("$status", assignee == null ? 0 : 1), ("$now", now));
                return Add(Find(id)!, "created", actor, name, text, false, origin, now, file);
            }

            if (ticket.Status >= AhelpStatus.Closing && (!drain || ticket.Status != AhelpStatus.Closing))
                return new Result("closed");
            if (staff ? !admin : actor != ticket.Player && (!admin || actor != ticket.Assignee))
                return new Result("forbidden");
            return Add(ticket, "message", actor, name, text, staff, origin, now, file);
        });

    public Result Change(Guid id, Guid actor, string name, AhelpAction action, string reason, string origin, long now)
        => Write(() =>
        {
            if (FindEvent(origin) is { } prior)
                return new Result("", prior);
            var ticket = Find(id);
            if (ticket == null)
                return new Result("missing");
            if (action == AhelpAction.Claim)
            {
                if (ticket.Player == actor)
                    return new Result("forbidden");
                if (ticket.Status != AhelpStatus.New)
                    return new Result("already_claimed");
                Execute("UPDATE tickets SET status=1,assignee=$actor,assignee_name=$name WHERE id=$id",
                    ("$actor", actor.ToString()), ("$name", name), ("$id", id.ToString()));
                return Add(Find(id)!, "claimed", actor, name, "", false, origin, now);
            }

            if (ticket.Status != AhelpStatus.Claimed)
                return new Result("not_claimed");
            if (ticket.Assignee != actor)
                return new Result("forbidden");
            var outcome = action switch
            {
                AhelpAction.Resolve => AhelpStatus.Resolved,
                AhelpAction.Reject => AhelpStatus.Rejected,
                AhelpAction.Close => AhelpStatus.Closed,
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
            if (action == AhelpAction.Reject && string.IsNullOrWhiteSpace(reason))
                return new Result("reason_required");
            Execute("""
                UPDATE tickets SET status=CASE WHEN relay=1 THEN 2 ELSE $outcome END,
                outcome=$outcome,reason=$reason,terminal=CASE WHEN relay=0 THEN $now ELSE NULL END WHERE id=$id
                """, ("$id", id.ToString()), ("$outcome", (int) outcome), ("$reason", reason), ("$now", now));
            var updated = Find(id)!;
            return Add(updated, updated.Status == AhelpStatus.Closing ? "closing" : "finished", actor, name, reason, false, origin, now);
        });

    public Result Complete(Guid id, long now)
        => Write(() =>
        {
            var ticket = Find(id);
            if (ticket?.Status != AhelpStatus.Closing || ticket.Assignee == null)
                return new Result("");
            Execute("UPDATE tickets SET status=outcome,terminal=$now WHERE id=$id", ("$now", now), ("$id", id.ToString()));
            using var cmd = Command("SELECT reason FROM tickets WHERE id=$id", ("$id", id.ToString()));
            var reason = (string) cmd.ExecuteScalar()!;
            return Add(Find(id)!, "finished", ticket.Assignee.Value, ticket.AssigneeName ?? "", reason, false, $"final:{id}", now);
        });

    public bool Reserve(Guid id)
    {
        lock (_lock)
            return Execute("UPDATE tickets SET relay=1 WHERE id=$id AND status<2", ("$id", id.ToString())) == 1;
    }

    public Result Release(Guid ticket, Guid assignee, long now)
        => Write(() =>
        {
            if (Execute("""
                UPDATE tickets SET status=0,assignee=NULL,assignee_name=NULL,queued=$now
                WHERE id=$id AND status=1 AND assignee=$actor
                """, ("$id", ticket.ToString()), ("$actor", assignee.ToString()), ("$now", now)) == 0)
                return new Result("");
            return Add(Find(ticket)!, "released", Guid.Empty, "", "", false, $"release:{Guid.NewGuid()}", now);
        });

    public Result Presence(Guid player, string name, string text, long now)
        => Write(() => Find(Guid.Empty, player) is { Status: < AhelpStatus.Closing } ticket
            ? Add(ticket, "message", Guid.Empty, name, text, true, $"presence:{Guid.NewGuid()}", now)
            : new Result(""));

    public HashSet<Guid>? ReminderAssignees(long cutoff)
    {
        lock (_lock)
        {
            using var cmd = Command("""
                SELECT DISTINCT active.assignee
                FROM (SELECT 1 FROM tickets WHERE status=$new AND queued<=$cutoff LIMIT 1)
                LEFT JOIN tickets active ON active.status=$claimed AND active.assignee IS NOT NULL
                """, ("$cutoff", cutoff), ("$new", (int) AhelpStatus.New), ("$claimed", (int) AhelpStatus.Claimed));
            using var rows = cmd.ExecuteReader();
            if (!rows.Read())
                return null;

            var assignees = new HashSet<Guid>();
            do
            {
                if (!rows.IsDBNull(0))
                    assignees.Add(Guid.Parse(rows.GetString(0)));
            } while (rows.Read());
            return assignees;
        }
    }

    public AhelpSnapshot Read(Guid actor, bool admin, Guid ticket, Guid player, long before, string error = "", Guid operation = default)
        => Write(() =>
        {
            var tickets = new List<AhelpTicket>();
            using (var cmd = Command($"""
                SELECT {TicketColumns},EXISTS(SELECT 1 FROM events e WHERE e.ticket=t.id AND ($admin OR e.staff=0)
                AND e.author<>$actor AND e.id>COALESCE((SELECT seen FROM reads WHERE ticket=t.id AND reader=$actor),0))
                FROM tickets t WHERE t.status<3 AND ($admin OR t.player=$actor)
                ORDER BY t.status,t.created LIMIT 500
                """, ("$admin", admin), ("$actor", actor.ToString())))
            using (var rows = cmd.ExecuteReader())
            {
                while (rows.Read())
                    tickets.Add(Ticket(rows) with { Unread = rows.GetBoolean(8) });
            }
            var selected = admin ? (ticket != Guid.Empty ? Find(ticket) : Find(Guid.Empty, player)) : tickets.FirstOrDefault();
            if (selected is null || selected.Status >= AhelpStatus.Resolved)
                return new AhelpSnapshot(true, tickets.ToArray(), null, [], 0, error, operation);
            var lines = new List<AhelpLine>();
            using (var cmd = Command($"""
                SELECT {EventColumns},{TicketColumns} FROM events e JOIN tickets t ON e.ticket=t.id
                WHERE e.ticket=$id AND ($admin OR e.staff=0) AND e.kind IN ('created','message')
                AND ($before=0 OR e.id<$before) ORDER BY e.id DESC LIMIT $limit
                """, ("$id", selected.Id.ToString()), ("$admin", admin), ("$before", before), ("$limit", AhelpLimits.Page + 1)))
            using (var rows = cmd.ExecuteReader())
            {
                while (rows.Read())
                    lines.Add(Event(rows).Line);
            }
            var more = lines.Count > AhelpLimits.Page;
            if (more) lines.RemoveAt(lines.Count - 1);
            if (before == 0 && lines.Count > 0)
            {
                Execute("""
                    INSERT INTO reads VALUES ($ticket,$actor,$seen)
                    ON CONFLICT(ticket,reader) DO UPDATE SET seen=MAX(seen,excluded.seen)
                    """, ("$ticket", selected.Id.ToString()), ("$actor", actor.ToString()), ("$seen", lines[0].Id));
                var index = tickets.FindIndex(t => t.Id == selected.Id);
                if (index >= 0) tickets[index] = tickets[index] with { Unread = false };
            }
            lines.Reverse();
            return new AhelpSnapshot(true, tickets.ToArray(), selected, lines.ToArray(), more ? lines[0].Id : 0, error, operation);
        });

    public Attachment? ReadAttachment(long id, Guid actor, bool admin, bool relay)
    {
        lock (_lock)
        {
            using var cmd = Command("""
                SELECT e.file,e.mime,e.data FROM events e JOIN tickets t ON e.ticket=t.id
                WHERE e.id=$id AND e.data IS NOT NULL AND ($relay OR t.status<3)
                AND ($admin OR t.player=$actor AND e.staff=0)
                """, ("$id", id), ("$actor", actor.ToString()), ("$admin", admin), ("$relay", relay));
            using var rows = cmd.ExecuteReader();
            return rows.Read() ? new Attachment(rows.GetString(0), rows.GetString(1), (byte[]) rows.GetValue(2)) : null;
        }
    }

    private Entry[] ReadEvents(string where, params (string, object?)[] parameters)
    {
        using var cmd = Command($"SELECT {EventColumns},{TicketColumns} FROM events e JOIN tickets t ON e.ticket=t.id WHERE {where}", parameters);
        using var rows = cmd.ExecuteReader();
        var entries = new List<Entry>();
        while (rows.Read()) entries.Add(Event(rows));
        return entries.ToArray();
    }

    public Entry[] Pending(long now)
    {
        lock (_lock)
            return ReadEvents("""
                NOT EXISTS(SELECT 1 FROM relay_delivery d WHERE d.event=e.id AND (d.done=1 OR d.retry>$now))
                AND NOT EXISTS(
                    SELECT 1 FROM events prior LEFT JOIN relay_delivery delivery ON delivery.event=prior.id
                    WHERE prior.ticket=e.ticket AND prior.id<e.id AND COALESCE(delivery.done,0)=0)
                AND (e.kind NOT IN ('closing','finished') OR NOT EXISTS(
                    SELECT 1 FROM relay_media media JOIN events prior ON prior.id=media.event
                    WHERE prior.ticket=e.ticket AND prior.id<e.id))
                ORDER BY e.id LIMIT 20
                """, ("$now", now));
    }

    public Entry[] PendingMedia(long now)
    {
        lock (_lock)
            return ReadEvents("""
                e.id IN (SELECT event FROM relay_media WHERE retry<=$now)
                ORDER BY e.id LIMIT 20
                """, ("$now", now));
    }

    public bool QueueMedia(long id) => Write(() =>
    {
        var added = Execute("""
            INSERT OR IGNORE INTO relay_media(event)
            SELECT e.id FROM events e WHERE e.id=$id AND e.file IS NOT NULL
            AND e.kind IN ('created','message') AND e.origin NOT LIKE 'discord:%'
            AND NOT EXISTS(SELECT 1 FROM relay_delivery d WHERE d.event=e.id AND d.done=1)
            """, ("$id", id)) > 0;
        if (added)
            Ack(id, true, 0);
        return added;
    });

    public void AckMedia(long id, bool done, long retry)
    {
        lock (_lock)
        {
            if (done)
                Execute("DELETE FROM relay_media WHERE event=$id", ("$id", id));
            else
                Execute("UPDATE relay_media SET retry=$retry WHERE event=$id", ("$id", id), ("$retry", retry));
        }
    }

    public void Ack(long id, bool done, long retry)
    {
        lock (_lock)
            Execute("""
                INSERT INTO relay_delivery SELECT id,$done,$retry FROM events WHERE id=$id
                ON CONFLICT(event) DO UPDATE SET done=MAX(done,excluded.done),retry=excluded.retry
                """, ("$id", id), ("$done", done), ("$retry", retry));
    }

    public Entry[] Rewards()
    {
        lock (_lock)
            return ReadEvents("e.kind='finished' AND t.status=3 AND e.rewarded=0 ORDER BY e.id LIMIT 10");
    }

    public Entry[] History(long number, long before)
    {
        lock (_lock)
            return ReadEvents("t.number=$number AND ($before=0 OR e.id<$before) ORDER BY e.id DESC LIMIT 30",
                ("$number", number), ("$before", before));
    }

    public void MarkReward(long id)
    {
        lock (_lock)
            Execute("UPDATE events SET rewarded=1 WHERE id=$id", ("$id", id));
    }

    public Entry[] Results(Guid player)
    {
        lock (_lock)
            return ReadEvents("""
                t.player=$player AND e.kind='finished' AND e.id>COALESCE(
                (SELECT seen FROM reads WHERE ticket=t.id AND reader=$player),0)
                ORDER BY e.id LIMIT 20
                """, ("$player", player.ToString()));
    }

    public void MarkResult(Entry entry)
    {
        lock (_lock)
            Execute("""
                INSERT INTO reads VALUES ($ticket,$player,$seen)
                ON CONFLICT(ticket,reader) DO UPDATE SET seen=MAX(seen,excluded.seen)
                """, ("$ticket", entry.Ticket.Id.ToString()), ("$player", entry.Ticket.Player.ToString()), ("$seen", entry.Id));
    }

    public void Dispose()
    {
        lock (_lock) _connection.Dispose();
    }
}
