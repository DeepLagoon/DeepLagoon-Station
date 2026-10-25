using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using IPlayerLocator = Content.Server.Administration.IPlayerLocator;
using Content.Server.Administration.Managers;
using Content.Server.Administration.Notes;
using Content.Server.Database;
using Content.Shared.Administration;
using Content.Shared.Administration.Notes;
using Content.Shared.Database;
using Content.Shared.CCVar;
using Content.Shared.Roles;
using Robust.Server.ServerStatus;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using DbAdmin = Content.Server.Database.Admin;

namespace Content.Server._DeepLagoon.DiscordLink;

public sealed partial class DiscordLinkSystem
{
    [Dependency] private IBanManager _bans = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IAdminNotesManager _notes = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IPlayerLocator _locator = default!;

    // This route shares the loopback-only bot token gate, never the enrollment key.
    // The bot verifies Discord roles and resolves canonical UIDs through the main slot.
    private async Task<bool> HandleModerationApi(IStatusHandlerContext context)
    {
        ModerationRequest? request;
        try
        {
            var buffer = new byte[32769];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await context.RequestBody.ReadAsync(buffer.AsMemory(count));
                if (read == 0) break;
                count += read;
            }
            request = count > 32768 ? null : JsonSerializer.Deserialize<ModerationRequest>(buffer.AsSpan(0, count));
        }
        catch (JsonException) { request = null; }
        if (request == null || !Guid.TryParse(request.Uid, out var uid) ||
            !Guid.TryParse(request.ActorUid, out var actorUid) || uid == Guid.Empty || actorUid == Guid.Empty)
        {
            await context.RespondJsonAsync(new { error = "invalid_request" }, HttpStatusCode.BadRequest);
            return true;
        }
        await _apiLock.WaitAsync();
        try
        {
            var result = await OnMainThread(() => Moderate(request, new NetUserId(uid), new NetUserId(actorUid)));
            await context.RespondJsonAsync(result.Body, result.Status);
        }
        catch (Exception e)
        {
            Log.Error($"Discord moderation failed: {e.GetType().Name}");
            await context.RespondJsonAsync(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
        }
        finally { _apiLock.Release(); }
        return true;
    }

    private static ApiResult ModerationError(string error)
        => new(HttpStatusCode.Conflict, new { error });

    private async Task<ApiResult> Moderate(ModerationRequest request, NetUserId uid, NetUserId actor)
    {
        if (!_enabled) return ModerationError("unavailable");
        if (_store?.MergePending(uid.UserId) == true) return ModerationError("merge_pending");
        var admin = await _database.GetAdminDataForAsync(uid);
        if (request.Operation is "admin_flags" or "staff_flags")
            return await ChangeDiscordAdminFlag(request, uid, actor, admin);
        if (request.Operation == "staff_status")
        {
            var rank = admin?.AdminRankId is {} rankId ? await _database.GetAdminRankAsync(rankId) : null;
            var kind = rank?.Name switch { "Game Admin" => "admin", "Discord Trial Admin" => "trial", "Discord Mapper" => "mapper", _ => ManagedKind(admin) };
            return new ApiResult(HttpStatusCode.OK, new { kind, uid = uid.ToString() });
        }
        if (request.Operation == "jobs")
            return new ApiResult(HttpStatusCode.OK, new { rows = _prototypes.EnumeratePrototypes<JobPrototype>()
                .Select(j => new { id = j.ID, name = Loc.GetString(j.Name) }).OrderBy(j => j.name).ToArray() });
        var player = await _database.GetPlayerRecordByUserId(uid);
        var author = await _database.GetPlayerRecordByUserId(actor);
        if (player == null || author == null) return ModerationError("player_not_in_slot");
        if (request.Operation == "bans")
        {
            var bans = await _database.GetServerBansAsync(null, uid, null, null);
            var roles = await _database.GetServerRoleBansAsync(null, uid, null, null, true);
            return new ApiResult(HttpStatusCode.OK, new {
                bans = bans.Select(b => new { id = b.Id, reason = b.Reason, expires = b.ExpirationTime, pardoned = b.Unban != null }),
                job_bans = roles.Select(b => new { id = b.Id, job = b.Role, reason = b.Reason, expires = b.ExpirationTime, pardoned = b.Unban != null }) });
        }
        if (request.Operation == "notes")
            return new ApiResult(HttpStatusCode.OK, new { rows = (await _notes.GetAllAdminRemarks(uid.UserId))
                .OfType<AdminNoteRecord>().Where(n => !n.Deleted).Select(n => new {
                    id = n.Id, message = n.Message, severity = n.Severity.ToString(), secret = n.Secret,
                    expires = n.ExpirationTime, author = n.CreatedBy?.LastSeenUserName, created = n.CreatedAt }).ToArray() });
        if (request.Operation is "staff_add" or "staff_remove")
            return await ChangeDiscordStaff(request, uid, admin, request.ActorName ?? author.LastSeenUserName);

        var severityName = request.Severity == "Default" ? request.Operation switch
        {
            "ban" => _config.GetCVar(CCVars.ServerBanDefaultSeverity),
            "job_ban" => _config.GetCVar(CCVars.RoleBanDefaultSeverity),
            _ => "Medium"
        } : request.Severity;
        if (!Enum.TryParse<NoteSeverity>(severityName, true, out var severity) || !Enum.IsDefined(severity) ||
            request.Minutes > 5256000) return ModerationError("invalid_request");
        var message = request.Message?.Trim() ?? "";
        if (request.Operation is "ban" or "job_ban" or "note_add" or "note_edit" &&
            (message.Length == 0 || message.Length > 2000)) return ModerationError("invalid_request");
        var expiry = request.Minutes > 0 ? DateTime.UtcNow.AddMinutes(request.Minutes) : (DateTime?) null;
        switch (request.Operation)
        {
            case "ban":
                var banTarget = await _locator.LookupIdByNameOrIdAsync(uid.ToString());
                if (banTarget == null) return ModerationError("player_not_in_slot");
                await _bans.CreateServerBanAsync(uid, player.LastSeenUserName, actor, null, banTarget.LastHWId,
                    request.Minutes, severity, message);
                break;
            case "job_ban":
                if (request.Job == null || !_prototypes.HasIndex<JobPrototype>(request.Job)) return ModerationError("invalid_job");
                var jobTarget = await _locator.LookupIdByNameOrIdAsync(uid.ToString());
                if (jobTarget == null) return ModerationError("player_not_in_slot");
                await _bans.CreateRoleBanAsync(uid, player.LastSeenUserName, actor, null, jobTarget.LastHWId,
                    request.Job, request.Minutes, severity, message, DateTimeOffset.UtcNow);
                break;
            case "unban":
                var ban = await _database.GetServerBanAsync(request.Id);
                if (ban == null || ban.UserId != uid) return ModerationError("record_not_found");
                if (ban.Unban == null)
                    await _database.AddServerUnbanAsync(new ServerUnbanDef(request.Id, actor, DateTimeOffset.UtcNow));
                break;
            case "job_unban":
                var roleBan = await _database.GetServerRoleBanAsync(request.Id);
                if (roleBan == null || roleBan.UserId != uid) return ModerationError("record_not_found");
                await _bans.PardonRoleBan(request.Id, actor, DateTimeOffset.UtcNow);
                break;
            case "note_add":
                await _notes.AddAdminRemark(actor, author.LastSeenUserName, uid.UserId, NoteType.Note, message, severity, request.Secret, expiry);
                break;
            case "note_edit":
            case "note_remove":
                var note = await _database.GetAdminNote(request.Id);
                if (note == null || note.Player?.UserId != uid || note.Deleted) return ModerationError("record_not_found");
                if (request.Operation == "note_remove")
                    await _notes.DeleteAdminRemark(request.Id, NoteType.Note, actor, author.LastSeenUserName);
                else
                    await _notes.ModifyAdminRemark(request.Id, NoteType.Note, actor, author.LastSeenUserName, message, severity, request.Secret, expiry);
                break;
            default: return ModerationError("invalid_request");
        }
        Log.Info($"Discord moderation: {request.Operation} target={uid} actor={actor} record={request.Id}");
        return new ApiResult(HttpStatusCode.OK, new { ok = true, uid = uid.ToString() });
    }

    private string? ManagedKind(DbAdmin? admin)
    {
        if (admin == null) return null;
        var grant = _store?.StaffGrant(admin.UserId);
        return grant != null && grant.Value.RankId == admin.AdminRankId ? grant.Value.Kind : null;
    }

    private async Task<ApiResult> ChangeDiscordStaff(ModerationRequest request, NetUserId uid, DbAdmin? admin, string actorName)
    {
        if (request.Kind is not ("trial" or "admin" or "mapper")) return ModerationError("invalid_request");
        var current = ManagedKind(admin);
        // Narrow commands must not overwrite an unrelated rank, suspension or individual flags.
        if (admin != null && (current == null || admin.Suspended || admin.Flags.Count > 0))
            return ModerationError("staff_conflict");
        if (request.Operation == "staff_remove")
        {
            if (admin != null && current != request.Kind) return ModerationError("staff_conflict");
            if (admin != null) await _database.RemoveAdminAsync(uid, actorName: actorName);
            _store!.RemoveStaffGrant(uid.UserId);
        }
        else
        {
            var (_, ranks) = await _database.GetAllAdminAndRanksAsync();
            AdminRank? rank;
            if (request.Kind == "admin")
            {
                rank = ranks.SingleOrDefault(r => r.Name == "Game Admin");
                if (rank == null) return ModerationError("game_admin_rank_missing");
            }
            else
            {
                var name = request.Kind == "trial" ? "Discord Trial Admin" : "Discord Mapper";
                var flags = AdminFlags.Admin | AdminFlags.Adminchat | AdminFlags.Adminhelp | AdminFlags.AdminWho |
                            AdminFlags.Debug | AdminFlags.Mapping | AdminFlags.Moderator | AdminFlags.Server |
                            AdminFlags.Spawn | AdminFlags.Stealth | AdminFlags.ViewNotes;
                if (request.Kind == "trial")
                {
                    var baseRank = ranks.SingleOrDefault(r => r.Name == "Game Admin");
                    if (baseRank == null) return ModerationError("game_admin_rank_missing");
                    flags = AdminFlagsHelper.NamesToFlags(baseRank.Flags.Select(f => f.Flag)) &
                            ~(AdminFlags.Debug | AdminFlags.Permissions);
                }
                rank = ranks.SingleOrDefault(r => r.Name == name);
                var expectedFlags = AdminFlagsHelper.FlagsToNames(flags).ToHashSet();
                if (rank != null && !expectedFlags.SetEquals(rank.Flags.Select(f => f.Flag)))
                {
                    // Upgrade only the old bot-created mapping-only rank; preserve custom ranks.
                    if (request.Kind != "mapper" || !rank.Flags.Select(f => f.Flag).ToHashSet().SetEquals(new[] { "MAPPING" }))
                        return ModerationError("staff_rank_conflict");
                    rank.Flags = expectedFlags.Select(f => new AdminRankFlag { Flag = f }).ToList();
                    await _database.UpdateAdminRankAsync(rank);
                    _admins.ReloadAdminsWithRank(rank.Id);
                }
                if (rank == null)
                {
                    rank = new AdminRank { Name = name, ShortName = request.Kind == "trial" ? "Trial" : "Mapper",
                        Flags = expectedFlags.Select(f => new AdminRankFlag { Flag = f }).ToList() };
                    await _database.AddAdminRankAsync(rank);
                    (_, ranks) = await _database.GetAllAdminAndRanksAsync();
                    rank = ranks.Single(r => r.Name == name);
                }
            }
            if (admin != null && current != request.Kind && !(current == "trial" && request.Kind == "admin"))
                return ModerationError("staff_conflict");
            var updated = admin ?? new DbAdmin { UserId = uid.UserId, Flags = new List<AdminFlag>() };
            updated.AdminRankId = rank.Id;
            updated.Title = request.Kind switch { "trial" => "Младший администратор", "mapper" => "Маппер", _ => "Game Admin" };
            if (admin == null) await _database.AddAdminAsync(updated, actorName: actorName);
            else await _database.UpdateAdminAsync(updated, actorName: actorName);
            _store!.SetStaffGrant(uid.UserId, request.Kind, rank.Id);
        }
        if (_players.TryGetSessionById(uid, out var session)) _admins.ReloadAdmin(session);
        Log.Info($"Discord staff: {request.Operation}/{request.Kind} target={uid} actor={request.ActorUid}");
        return new ApiResult(HttpStatusCode.OK, new { ok = true, uid = uid.ToString() });
    }

    private static AdminFlags EffectiveAdminFlags(DbAdmin admin)
    {
        var rank = AdminFlagsHelper.NamesToFlags(admin.AdminRank?.Flags.Select(f => f.Flag) ?? Array.Empty<string>());
        var positive = AdminFlagsHelper.NamesToFlags(admin.Flags.Where(f => !f.Negative).Select(f => f.Flag));
        var negative = AdminFlagsHelper.NamesToFlags(admin.Flags.Where(f => f.Negative).Select(f => f.Flag));
        return (rank | positive) & ~negative;
    }

    private async Task<ApiResult> ChangeDiscordAdminFlag(ModerationRequest request, NetUserId uid, NetUserId actor, DbAdmin? admin)
    {
        var author = await _database.GetAdminDataForAsync(actor);
        var authority = author is { Suspended: false } ? EffectiveAdminFlags(author) : AdminFlags.None;
        if ((authority & AdminFlags.Host) != 0) authority = AdminFlagsHelper.Everything;
        if ((authority & AdminFlags.Permissions) == 0) return ModerationError("permissions_denied");
        if (request.Operation == "admin_flags")
            return new ApiResult(HttpStatusCode.OK, new { rows = AdminFlagsHelper.FlagsToNames(authority) });
        var flagName = request.Flag?.Trim().ToUpperInvariant();
        if (flagName == null || !AdminFlagsHelper.FlagsToNames(AdminFlagsHelper.Everything).Contains(flagName) ||
            request.Mode is not ("grant" or "deny" or "inherit")) return ModerationError("invalid_request");
        if (admin == null) return ModerationError("admin_not_found");
        var flag = AdminFlagsHelper.NameToFlag(flagName);
        var targetFlags = AdminFlagsHelper.NamesToFlags(admin.AdminRank?.Flags.Select(f => f.Flag) ?? Array.Empty<string>()) |
                          AdminFlagsHelper.NamesToFlags(admin.Flags.Where(f => !f.Negative).Select(f => f.Flag));
        if (((targetFlags | flag) & ~authority) != 0) return ModerationError("permissions_denied");
        admin.Flags.RemoveAll(f => f.Flag == flagName);
        if (request.Mode != "inherit")
            admin.Flags.Add(new AdminFlag { Flag = flagName, Negative = request.Mode == "deny" });
        await _database.UpdateAdminAsync(admin, actorName: request.ActorName ?? actor.ToString());
        if (_players.TryGetSessionById(uid, out var session)) _admins.ReloadAdmin(session);
        Log.Info($"Discord permissions: target={uid} actor={actor} flag={flagName} mode={request.Mode}");
        return new ApiResult(HttpStatusCode.OK, new { ok = true, uid = uid.ToString(), flag = flagName,
            mode = request.Mode, flags = AdminFlagsHelper.FlagsToNames(EffectiveAdminFlags(admin)), suspended = admin.Suspended });
    }

    private sealed record ModerationRequest(string Operation, string Uid, string ActorUid,
        string? Message = null, uint Minutes = 0, string Severity = "Medium", string? Job = null,
        int Id = 0, bool Secret = false, string? Kind = null, string? ActorName = null,
        string? Flag = null, string? Mode = null);
}
