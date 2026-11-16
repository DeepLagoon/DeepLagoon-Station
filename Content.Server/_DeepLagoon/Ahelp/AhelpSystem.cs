using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Players.RateLimiting;
using Content.Server._DeepLagoon.Currency;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Players.RateLimiting;
using Content.Shared._DeepLagoon.Ahelp;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Ahelp;

public sealed partial class AhelpSystem : EntitySystem
{
    private const string MessageRate = "AhelpTicket";
    private const string ReadRate = "AhelpTicketRead";
    public const string Reminder = "Есть не взятые и никем не рассмотренные тикеты";

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPlayerLocator _locator = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private PlayerRateLimitManager _rate = default!;
    [Dependency] private LagoonCoinSystem _coins = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IGameTiming _timing = default!;

    private sealed class Upload(AhelpRequest request, TimeSpan expires)
    {
        public readonly AhelpRequest Request = request;
        public readonly byte[] Bytes = new byte[request.Size];
        public readonly bool[] Received = new bool[(request.Size + AhelpLimits.Chunk - 1) / AhelpLimits.Chunk];
        public readonly TimeSpan Expires = expires;
        public int Count;
    }

    private sealed record Download(long Id, byte[] Bytes, bool Admin, bool Preview = false)
    {
        public int Offset;
    }

    private readonly Dictionary<NetUserId, Upload> _uploads = new();
    private readonly Dictionary<NetUserId, Download> _downloads = new();
    private readonly HashSet<NetUserId> _previewJobs = new();
    private readonly HashSet<NetUserId> _working = new();
    private Lazy<AhelpStore>? _store;
    internal bool Enabled => _config.GetCVar(CCVars.AhelpTickets);
    internal AhelpStore Store => _store!.Value;
    private TimeSpan _nextReminder;
    private TimeSpan _nextMaintenance;
    private bool _maintaining;
    private bool _stopping;
    private long _reminderSequence;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public override void Initialize()
    {
        base.Initialize();
        var root = _resources.UserData.RootDir;
        var path = root == null ? ":memory:" : Path.Combine(root, "ahelp-tickets.db");
        _store = new Lazy<AhelpStore>(() => new AhelpStore(path));
        _rate.Register(MessageRate, new RateLimitRegistration(CCVars.AhelpRateLimitPeriod, CCVars.AhelpRateLimitCount, null));
        _rate.Register(ReadRate, new RateLimitRegistration(CCVars.AhelpRateLimitPeriod, CCVars.AhelpRateLimitCount, null));
        SubscribeNetworkEvent<AhelpRequest>(OnRequest);
        SubscribeNetworkEvent<AhelpUpload>(OnUpload);
        _players.PlayerStatusChanged += StatusChanged;
        RegisterRelay();
    }

    public override void Shutdown()
    {
        _stopping = true;
        _players.PlayerStatusChanged -= StatusChanged;
        if (_store?.IsValueCreated == true)
            _store.Value.Dispose();
        base.Shutdown();
    }

    private bool CanHelp(ICommonSession session) => _admins.GetAdminData(session)?.HasFlag(AdminFlags.Adminhelp) == true;

    private async Task<bool> CanHelpOffline(Guid actor)
    {
        var id = new NetUserId(actor);
        if (_players.TryGetSessionById(id, out var session) &&
            _admins.GetAdminData(session, includeDeAdmin: true)?.HasFlag(AdminFlags.Adminhelp) == true)
            return true;
        return DiscordApiAuthority.Has(await _db.GetAdminDataForAsync(id), "ADMINHELP");
    }

    private async void StatusChanged(object? sender, SessionStatusEventArgs args)
    {
        var session = args.Session;
        if (!Enabled || args.NewStatus is not (SessionStatus.InGame or SessionStatus.Disconnected))
            return;
        if (args.NewStatus == SessionStatus.Disconnected)
        {
            _uploads.Remove(session.UserId);
            _downloads.Remove(session.UserId);
        }

        try
        {
            var text = args.NewStatus == SessionStatus.InGame ? "Подключился" : "Отключился";
            if (args.NewStatus == SessionStatus.Disconnected &&
                await _db.GetServerBanAsync(null, session.UserId, null, null) is { } ban)
                text = "Заблокирован: " + ban.Reason;
            var entry = await Task.Run(() => Store.Presence(session.UserId.UserId, session.Name, text, Now));
            Publish(entry);
            if (args.NewStatus != SessionStatus.InGame || _stopping)
                return;
            foreach (var result in await Task.Run(() => Store.Results(session.UserId.UserId)))
                await DeliverResult(session, result);
        }
        catch (Exception ex) { Log.Error($"Ahelp presence update failed: {ex.GetType().Name}"); }
    }

    private async void OnRequest(AhelpRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!Enabled || _stopping)
            return;
        if (!Enum.IsDefined(request.Action) || request.Text == null || request.File == null ||
            request.Text.Length > AhelpLimits.Text || request.File.Length > 250 ||
            request.Size is < 0 or > AhelpLimits.Image)
            return;
        var mutation = request.Action is not (AhelpAction.Read or AhelpAction.Attachment or AhelpAction.Preview);
        if (_rate.CountAction(session, mutation ? MessageRate : ReadRate) != RateLimitStatus.Allowed)
        {
            if (mutation) Error(session, request.Operation, "rate_limited");
            return;
        }
        if (mutation && request.Operation == Guid.Empty)
            return;
        if (request.Action == AhelpAction.Preview)
        {
            await SendPreview(session, request.Attachment);
            return;
        }
        if (request.Action == AhelpAction.Send && request.Size > 0)
        {
            if (_uploads.Count >= 6 && !_uploads.ContainsKey(session.UserId) || _working.Contains(session.UserId))
            {
                Error(session, request.Operation, "busy");
                return;
            }
            _uploads[session.UserId] = new Upload(request, _timing.RealTime + TimeSpan.FromMinutes(2));
            return;
        }

        await Handle(session, request);
    }

    private async void OnUpload(AhelpUpload chunk, EntitySessionEventArgs args)
    {
        var user = args.SenderSession.UserId;
        if (!Enabled || !_uploads.TryGetValue(user, out var upload) || upload.Request.Operation != chunk.Operation ||
            chunk.Offset < 0 || chunk.Offset >= upload.Bytes.Length || chunk.Offset % AhelpLimits.Chunk != 0 ||
            chunk.Bytes.Length != Math.Min(upload.Bytes.Length - chunk.Offset, AhelpLimits.Chunk))
            return;
        var index = chunk.Offset / AhelpLimits.Chunk;
        if (upload.Received[index])
            return;
        chunk.Bytes.CopyTo(upload.Bytes, chunk.Offset);
        upload.Received[index] = true;
        if (++upload.Count != upload.Received.Length)
            return;
        _uploads.Remove(user);
        await Handle(args.SenderSession, upload.Request, upload.Bytes);
    }

    private async Task Handle(ICommonSession session, AhelpRequest request, byte[]? bytes = null)
    {
        if (!_working.Add(session.UserId))
        {
            Error(session, request.Operation, "busy");
            return;
        }
        var actor = session.UserId.UserId;
        var admin = CanHelp(session);
        var target = admin && request.Player != Guid.Empty ? request.Player : actor;
        try
        {
            if (request.Action == AhelpAction.Read)
            {
                var snapshot = await Task.Run(() => Store.Read(actor, admin, request.Ticket, request.Player, request.Before,
                    operation: request.Operation));
                if (CanHelp(session) == admin) Reply(session, snapshot);
                return;
            }
            if (request.Action == AhelpAction.Attachment)
            {
                if (_downloads.Count >= 6 && !_downloads.ContainsKey(session.UserId))
                {
                    Error(session, request.Operation, "busy");
                    return;
                }
                var file = await Task.Run(() => Store.ReadAttachment(request.Attachment, actor, admin, false));
                if (file != null && CanHelp(session) == admin)
                    _downloads[session.UserId] = new Download(request.Attachment, file.Bytes, admin);
                else
                    Error(session, request.Operation, "forbidden");
                return;
            }

            var origin = $"game:{actor}:{request.Operation}";
            var text = request.Text.Trim();
            var name = session.Name;
            AhelpStore.Result result;
            if (request.Action == AhelpAction.Send)
            {
                if (text.Length == 0 && bytes == null)
                    return;
                var targetName = target == actor ? name :
                    _players.TryGetSessionById(new NetUserId(target), out var player) ? player.Name :
                    (await _locator.LookupIdAsync(new NetUserId(target)))?.Username;
                if (targetName == null)
                {
                    Error(session, request.Operation, "missing");
                    return;
                }
                var file = bytes == null ? null : await Task.Run(() => AhelpImage.Read(request.File, bytes));
                var authorized = CanHelp(session);
                if (authorized != admin)
                {
                    Error(session, request.Operation, "forbidden");
                    return;
                }
                result = await Task.Run(() => Store.Send(request.Ticket, target, targetName, actor, name, admin,
                    request.Staff, text, origin, Now, file));
            }
            else if (admin)
            {
                result = await Task.Run(() => Store.Change(request.Ticket, actor, name, request.Action, text, origin, Now));
            }
            else
            {
                result = new AhelpStore.Result("forbidden");
            }

            Publish(result, request.Sound);
            var selected = result.Event?.Ticket.Id ?? request.Ticket;
            var state = await Task.Run(() => Store.Read(actor, admin, selected, target, 0, result.Error, request.Operation));
            if (CanHelp(session) == admin) Reply(session, state);
        }
        catch (Exception ex)
        {
            if (bytes == null)
                Log.Error($"Ahelp action failed: {ex.GetType().Name}");
            Error(session, request.Operation, bytes == null ? "unavailable" : "invalid_image");
        }
        finally { _working.Remove(session.UserId); }
    }

    private async Task SendPreview(ICommonSession session, long id)
    {
        var user = session.UserId;
        if (_downloads.Count >= 6 || _downloads.ContainsKey(user) || _previewJobs.Count >= 3 || !_previewJobs.Add(user))
        {
            Reply(session, new AhelpDownload(id, 0, 0, [], preview: true));
            return;
        }
        try
        {
            var admin = CanHelp(session);
            var preview = await Task.Run(() => Store.ReadAttachment(id, user.UserId, admin, false) is { } file
                ? AhelpImage.Preview(file) : null);
            if (preview == null || !session.Channel.IsConnected || CanHelp(session) != admin ||
                !_downloads.TryAdd(user, new Download(id, preview, admin, Preview: true)))
                Reply(session, new AhelpDownload(id, 0, 0, [], preview: true));
        }
        catch (Exception ex)
        {
            Log.Debug($"Ahelp preview unavailable: {ex.GetType().Name}");
            Reply(session, new AhelpDownload(id, 0, 0, [], preview: true));
        }
        finally { _previewJobs.Remove(user); }
    }

    private void Reply(ICommonSession session, EntityEventArgs message)
    {
        if (!_stopping && session.Status is SessionStatus.InGame or SessionStatus.Connected)
            RaiseNetworkEvent(message, session.Channel);
    }

    private void Error(ICommonSession session, Guid operation, string error)
        => Reply(session, new AhelpSnapshot(true, [], null, [], 0, error, operation));

    internal void Publish(AhelpStore.Result result, bool sound = true)
    {
        if (_stopping || !result.Applied || result.Event is not { } entry)
            return;
        NotifyRelay();
        var ticket = entry.Ticket;
        Log.Info($"Ahelp #{ticket.Number} ({ticket.Id}) event {entry.Id} {entry.Kind} by {entry.Line.Author}");
        var adminSound = entry.Kind == "created" && entry.Line.Author == ticket.Player;
        var publicReply = entry.Kind is "message" or "created" && entry.Line.Author != ticket.Player && !entry.Line.Staff;
        foreach (var admin in _admins.ActiveAdmins)
        {
            if (admin.UserId.UserId != ticket.Player && CanHelp(admin))
                Reply(admin, new AhelpNotice(ticket.Id, entry.Id, adminSound, false, false));
        }
        if (!_players.TryGetSessionById(new NetUserId(ticket.Player), out var player))
            return;
        if (entry.Kind == "finished")
            SendResult(player, entry);
        else if (!entry.Line.Staff)
            Reply(player, new AhelpNotice(ticket.Id, entry.Id, false, publicReply && sound, false));
    }

    private async void SendResult(ICommonSession session, AhelpStore.Entry entry)
    {
        try { await DeliverResult(session, entry); }
        catch (Exception ex) { Log.Error($"Ahelp result delivery failed: {ex.GetType().Name}"); }
    }

    private async Task DeliverResult(ICommonSession session, AhelpStore.Entry entry)
    {
        if (session.Status != SessionStatus.InGame)
            return;
        var outcome = entry.Ticket.Status switch
        {
            AhelpStatus.Resolved => "решён",
            AhelpStatus.Rejected => $"отклонён. {entry.Line.Text}",
            _ => "закрыт",
        };
        var message = $"Тикет ahelp #{entry.Ticket.Number} {outcome}";
        Reply(session, new AhelpNotice(entry.Ticket.Id, entry.Id, false, false, false, message));
        _chat.DispatchServerMessage(session, message, suppressLog: true);
        await Task.Run(() => Store.MarkResult(entry));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!Enabled)
            return;
        foreach (var (user, download) in _downloads.ToArray())
        {
            if (!_players.TryGetSessionById(user, out var session) || download.Admin && !CanHelp(session))
            {
                _downloads.Remove(user);
                continue;
            }
            var end = Math.Min(download.Offset + AhelpLimits.Chunk, download.Bytes.Length);
            Reply(session, new AhelpDownload(download.Id, download.Offset, download.Bytes.Length,
                download.Bytes[download.Offset..end], download.Preview));
            download.Offset = end;
            if (end == download.Bytes.Length) _downloads.Remove(user);
        }
        if (_maintaining || _timing.RealTime < _nextMaintenance)
            return;
        _nextMaintenance = _timing.RealTime + TimeSpan.FromSeconds(5);
        foreach (var (user, upload) in _uploads.ToArray())
        {
            if (_timing.RealTime < upload.Expires) continue;
            _uploads.Remove(user);
            if (_players.TryGetSessionById(user, out var session))
                Error(session, upload.Request.Operation, "upload_expired");
        }
        Maintain();
    }

    private async void Maintain()
    {
        _maintaining = true;
        try
        {
            var cutoff = Now - Math.Max(1, _config.GetCVar(CCVars.AhelpReminderSeconds));
            var admins = _admins.ActiveAdmins.Where(CanHelp).ToArray();
            if (admins.Length != 0 && _timing.RealTime >= _nextReminder &&
                await Task.Run(() => Store.ReminderAssignees(cutoff)) is { } assignees)
            {
                var available = admins.Where(admin => CanHelp(admin) && !assignees.Contains(admin.UserId.UserId)).ToArray();
                if (available.Length != 0)
                {
                    _nextReminder = _timing.RealTime + TimeSpan.FromSeconds(Math.Max(1, _config.GetCVar(CCVars.AhelpReminderRepeatSeconds)));
                    var reminder = new AhelpNotice(Guid.Empty, --_reminderSequence, true, false, true);
                    foreach (var admin in available)
                    {
                        Reply(admin, reminder);
                        _chat.ChatMessageToOne(ChatChannel.AdminAlert, Reminder, Reminder, default, false, admin.Channel, Color.Red);
                    }
                }
            }
            foreach (var reward in await Task.Run(() => Store.Rewards()))
            {
                if (_stopping) break;
                await _coins.Grant(new NetUserId(reward.Line.Author), 1, $"Ahelp #{reward.Ticket.Number}", null,
                    $"ahelp:resolve:{reward.Ticket.Id}");
                await Task.Run(() => Store.MarkReward(reward.Id));
            }
            foreach (var ticket in await Task.Run(() => Store.Read(Guid.Empty, true, Guid.Empty, Guid.Empty, 0).Tickets))
            {
                if (ticket.Status == AhelpStatus.Claimed && ticket.Assignee is { } assignee && !await CanHelpOffline(assignee))
                    Publish(await Task.Run(() => Store.Release(ticket.Id, assignee, Now)));
            }
        }
        catch (Exception ex) { Log.Error($"Ahelp maintenance failed: {ex.GetType().Name}"); }
        finally { _maintaining = false; }
    }
}
