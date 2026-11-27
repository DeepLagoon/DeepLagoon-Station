using System.Threading.Tasks;
using Content.Shared.Administration.Notes;
using Content.Shared.Database;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    event Action<ModerationEvent>? ModerationChanged;
}

public sealed partial class ServerDbManager
{
    public event Action<ModerationEvent>? ModerationChanged;

    private void PublishModeration(ModerationEvent change)
    {
        if (ModerationChanged == null)
            return;
        foreach (Action<ModerationEvent> handler in ModerationChanged.GetInvocationList())
        {
            try { handler(change); }
            catch (Exception e)
            {
                // A notification failure must never turn a committed ban/note into a failed operation.
                _logMgr.GetSawmill("discord.moderation").Error($"Moderation notification failed: {e.GetType().Name}");
            }
        }
    }

    private async Task<int> AddModerationRemark(Func<Task<int>> write, ModerationEvent change)
    {
        var id = await write();
        PublishModeration(change with { Id = id });
        return id;
    }

    private async Task<ModerationEvent?> ReadModeration(NoteType type, int id)
    {
        // Use the ban definitions directly: a banned UID need not have a player record yet.
        if (type == NoteType.ServerBan)
        {
            var ban = await GetServerBanAsync(id);
            return ban == null ? null : new ModerationEvent(type, id, "", ban.UserId, null, ban.Reason,
                ban.Severity, (await GetServerBanAsNoteAsync(id))?.Deleted ?? true,
                ban.ExpirationTime, ban.RoundId, null, DateTimeOffset.UtcNow);
        }
        if (type == NoteType.RoleBan)
        {
            var ban = await GetServerRoleBanAsync(id);
            return ban == null ? null : new ModerationEvent(type, id, "", ban.UserId, null, ban.Reason,
                ban.Severity, (await GetServerRoleBanAsNoteAsync(id))?.Deleted ?? true,
                ban.ExpirationTime, ban.RoundId, new[] { ban.Role }, DateTimeOffset.UtcNow);
        }
        IAdminRemarksRecord? record = type switch
        {
            NoteType.Note => await GetAdminNote(id),
            NoteType.Watchlist => await GetAdminWatchlist(id),
            NoteType.Message => await GetAdminMessage(id),
            _ => null
        };
        if (record == null)
            return null;
        var severity = record switch
        {
            AdminNoteRecord n => (NoteSeverity?) n.Severity,
            _ => null
        };
        return new ModerationEvent(type, id, "", record.Player?.UserId, null, record.Message, severity,
            record is AdminWatchlistRecord || record is AdminNoteRecord { Secret: true },
            record.ExpirationTime, record.Round?.Id, null, DateTimeOffset.UtcNow);
    }

    private async Task<ModerationEvent?> TryReadModeration(NoteType type, int id)
    {
        try { return await ReadModeration(type, id); }
        catch (Exception e)
        {
            _logMgr.GetSawmill("discord.moderation").Error($"Could not read moderation notification: {e.GetType().Name}");
            return null;
        }
    }

    private async Task WriteModeration(Func<Task> write, NoteType type, int id, Guid? actor, string action)
    {
        // Capture deleted/hidden rows before the write. Never prevent a write because notification lookup failed.
        var before = ModerationChanged == null ? null : await TryReadModeration(type, id);
        await write();
        if (ModerationChanged == null)
            return;
        var after = action is "Изменено" or "Скрыто из заметок" ? await TryReadModeration(type, id) : before;
        if (after != null)
            PublishModeration(after with
            {
                Action = action, Actor = actor, Time = DateTimeOffset.UtcNow,
                PreviousMessage = action == "Изменено" && before?.Message != after.Message ? before?.Message : null,
                PreviousSecret = before?.Secret ?? true
            });
    }
}
