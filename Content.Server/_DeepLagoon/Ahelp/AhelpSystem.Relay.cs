using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.Ahelp;
using Content.Shared.CCVar;
using Robust.Server.ServerStatus;
using Robust.Shared.Asynchronous;

namespace Content.Server._DeepLagoon.Ahelp;

public sealed partial class AhelpSystem
{
    [Dependency] private IStatusHost _status = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private DiscordLinkSystem _identity = default!;
    private readonly SemaphoreSlim _relaySlots = new(2);
    private readonly SemaphoreSlim _relayPendingSlot = new(1);
    private readonly SemaphoreSlim _relayMediaPendingSlot = new(1);
    private readonly SemaphoreSlim _relayImageEncoders = new(2);
    private TaskCompletionSource _relayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly JsonSerializerOptions RelayJson = new(JsonSerializerDefaults.Web);

    private sealed record RelayRequest(string Action, Guid Ticket = default, long Event = 0, string Discord = "",
        string Request = "", bool AdminRole = false, bool Staff = false, string Text = "", string File = "",
        byte[]? Data = null, bool Drain = false, bool Wait = false, string Media = "");

    private void RegisterRelay() => _status.AddHandler(HandleRelay);

    private async Task<bool> HandleRelay(IStatusHandlerContext context)
    {
        if (context.Url.AbsolutePath != "/deeplagoon/ahelp")
            return false;
        context.ResponseHeaders["Cache-Control"] = "no-store";
        var token = _config.GetCVar(CCVars.AhelpBotToken);
        if (!Enabled || !IPAddress.IsLoopback(context.RemoteEndPoint.Address) || context.RequestMethod != HttpMethod.Post ||
            token.Length < 32 || !context.RequestHeaders.TryGetValue("Authorization", out var header) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header.ToString()), Encoding.UTF8.GetBytes("Bearer " + token)))
        {
            await context.RespondErrorAsync(HttpStatusCode.Unauthorized);
            return true;
        }
        if (!await _relaySlots.WaitAsync(0))
        {
            await context.RespondJsonAsync(new { error = "busy" }, HttpStatusCode.TooManyRequests);
            return true;
        }
        var reading = true;
        try
        {
            RelayRequest? request;
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                using var payload = new MemoryStream();
                int count;
                while ((count = await context.RequestBody.ReadAsync(buffer.AsMemory(0, 65536))) != 0)
                {
                    if (payload.Length + count > (AhelpLimits.Image + 2L) / 3 * 4 + 8192)
                    {
                        await context.RespondErrorAsync(HttpStatusCode.RequestEntityTooLarge);
                        return true;
                    }
                    payload.Write(buffer, 0, count);
                }

                request = JsonSerializer.Deserialize<RelayRequest>(payload.GetBuffer().AsSpan(0, (int) payload.Length), RelayJson);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
            if (request is null || request.Text == null || request.File == null || request.Discord == null || request.Request == null ||
                request.Text.Length > AhelpLimits.Text || request.File.Length > 250 || request.Data?.Length > AhelpLimits.Image)
            {
                await context.RespondJsonAsync(new { error = "invalid_request" }, HttpStatusCode.BadRequest);
                return true;
            }

            object result;
            if (request.Action is "pending" or "pending-media")
            {
                reading = false;
                _relaySlots.Release();
                var media = request.Action == "pending-media";
                var slot = media ? _relayMediaPendingSlot : _relayPendingSlot;
                if (request.Wait && !await slot.WaitAsync(0))
                {
                    await context.RespondJsonAsync(new { error = "busy" }, HttpStatusCode.TooManyRequests);
                    return true;
                }
                try
                {
                    var batch = await WaitForRelayEvents(media, request.Wait);
                    result = new { events = batch.Events, round = batch.Round, activeTickets = batch.Tickets,
                        mediaQueue = true, mediaPlaceholders = true };
                }
                finally { if (request.Wait) slot.Release(); }
            }
            else if (request.Action == "discord-image" && request.Event > 0)
            {
                reading = false;
                _relaySlots.Release();
                if (!await _relayImageEncoders.WaitAsync(0))
                {
                    await context.RespondJsonAsync(new { error = "busy" }, HttpStatusCode.TooManyRequests);
                    return true;
                }
                try
                {
                    var attachment = await Task.Run(() => Store.ReadAttachment(request.Event, Guid.Empty, true, true) is { } original
                        ? AhelpImage.DiscordPreview(original)
                        : null);
                    result = new { attachment };
                }
                finally { _relayImageEncoders.Release(); }
            }
            else
                result = await RelayOnMainThread(() => Relay(request));
            await context.RespondJsonAsync(JsonSerializer.SerializeToElement(result, RelayJson));
        }
        catch (JsonException)
        {
            await context.RespondJsonAsync(new { error = "invalid_request" }, HttpStatusCode.BadRequest);
        }
        catch (Exception ex)
        {
            Log.Error($"Ahelp relay failed: {ex.GetType().Name}");
            await context.RespondJsonAsync(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
        }
        finally { if (reading) _relaySlots.Release(); }
        return true;
    }

    private void NotifyRelay()
    {
        Interlocked.Exchange(ref _relayChanged,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private async Task<AhelpStore.RelayState> WaitForRelayEvents(bool media, bool wait)
    {
        // Observe the signal before reading so an insert between the two cannot be missed.
        var signal = Volatile.Read(ref _relayChanged).Task;
        await _roundCleanup;
        var events = await Task.Run(() => Store.RelayBatch(media, Now));
        if (!wait || events.Events.Length > 0)
            return events;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(media ? 1 : 4));
        try { await signal.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { }
        await _roundCleanup;
        return await Task.Run(() => Store.RelayBatch(media, Now));
    }

    private Task<object> RelayOnMainThread(Func<Task<object>> action)
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private async Task<object> Relay(RelayRequest request)
    {
        var generation = Volatile.Read(ref _roundGeneration);
        await _roundCleanup;
        if (generation != Volatile.Read(ref _roundGeneration))
            return new { error = "round_changed" };
        switch (request.Action)
        {
            case "ack" when request.Event > 0:
                await Task.Run(() => Store.Ack(request.Event, true, 0));
                return new { ok = true };
            case "defer" when request.Event > 0:
                await Task.Run(() => Store.Ack(request.Event, false, Now + 5));
                return new { ok = true };
            case "queue-media" when request.Event > 0:
                await Task.Run(() => Store.QueueMedia(request.Event));
                NotifyRelay();
                return new { ok = true };
            case "ack-media" when request.Event > 0:
                await Task.Run(() => Store.AckMedia(request.Event, true, 0));
                NotifyRelay();
                return new { ok = true };
            case "defer-media" when request.Event > 0:
                await Task.Run(() => Store.AckMedia(request.Event, false, Now + 1));
                return new { ok = true };
            case "reserve":
                return new { ok = await Task.Run(() => Store.Reserve(request.Ticket)) };
            case "attachment" when request.Event > 0:
                return new { attachment = await Task.Run(() => Store.ReadAttachment(request.Event, Guid.Empty, true, true)) };
            case "ticket":
                var current = await Task.Run(() => Store.Get(request.Ticket));
                var author = current == null ? null : _identity.AhelpDiscordId(current.Player);
                var assignee = current?.Assignee is { } id ? _identity.AhelpDiscordId(id) : null;
                return new { ticket = current, author, assignee };
            case "complete":
                Publish(await Task.Run(() => Store.Complete(request.Ticket, Now)));
                return new { ok = true };
        }

        if (request.Ticket == Guid.Empty || !ulong.TryParse(request.Discord, out var discord) || discord == 0 ||
            !ulong.TryParse(request.Request, out var operation) || operation == 0)
            return new { error = "invalid_request" };
        var ticket = await Task.Run(() => Store.Get(request.Ticket));
        if (ticket == null)
            return new { error = "missing" };
        var identity = _identity.AhelpIdentity(request.Discord);
        if (identity == null)
            return new { error = "not_linked" };
        var (uid, name) = identity.Value;
        var authorIdentity = _identity.AhelpDiscordId(ticket.Player) == request.Discord;
        var assigneeIdentity = ticket.Assignee is { } assigned && _identity.AhelpDiscordId(assigned) == request.Discord;
        if (authorIdentity)
            uid = ticket.Player;
        else if (assigneeIdentity)
            uid = ticket.Assignee!.Value;
        var admin = request.AdminRole && await CanHelpOffline(uid);
        if (request.AdminRole && !admin)
        {
            foreach (var candidate in _identity.AhelpUserIds(request.Discord))
            {
                if (!await CanHelpOffline(candidate)) continue;
                if (!authorIdentity && !assigneeIdentity) uid = candidate;
                admin = true;
                break;
            }
        }
        if (request.AdminRole && !admin && uid != ticket.Player)
            return new { error = "not_staff" };
        AhelpStore.Result result;
        var origin = $"discord:{discord}:{operation}";
        if (request.Action == "send")
        {
            var text = request.Text.Trim();
            if (request.Media is not ("" or "loading" or "ready" or "failed"))
                return new { error = "invalid_request" };
            if (request.Media is "ready" or "failed")
            {
                // A completion may only replace a reservation made by this Discord account.
                var pending = await Task.Run(() => Store.GetEvent(origin));
                if (pending == null || pending.Ticket.Id != ticket.Id ||
                    pending.Line.Mime != AhelpLimits.Loading && pending.Line.Mime != AhelpLimits.Failed)
                    return new { error = pending == null ? "missing" : "", ticket };
                AhelpStore.Attachment? completed = null;
                if (request.Media == "ready" && request.Data != null)
                {
                    try { completed = await Task.Run(() => AhelpImage.Read(request.File, request.Data)); }
                    catch (Exception ex) { Log.Debug($"Rejected Discord ahelp image: {ex.GetType().Name}"); }
                }
                result = await Task.Run(() => Store.CompleteAttachment(origin, completed));
                if (generation != Volatile.Read(ref _roundGeneration)) return new { error = "round_changed" };
                Publish(result, sound: false);
                return new { error = request.Media == "ready" && completed == null ? "invalid_image" : result.Error, ticket };
            }
            if (text.Length == 0 && request.Data == null && request.Media != "loading")
                return new { error = "invalid_request" };
            AhelpStore.Attachment? image;
            try { image = request.Media == "loading" ? new AhelpStore.Attachment(request.File, AhelpLimits.Loading, []) :
                request.Data == null ? null : await Task.Run(() => AhelpImage.Read(request.File, request.Data)); }
            catch (Exception ex)
            {
                Log.Debug($"Rejected Discord ahelp image: {ex.GetType().Name}");
                return new { error = "invalid_image" };
            }
            result = await Task.Run(() => Store.Send(ticket.Id, ticket.Player, ticket.PlayerName, uid, name, admin,
                request.Staff, text, origin, Now, image, request.Drain));
        }
        else
        {
            if (!admin)
                return new { error = "forbidden" };
            var action = request.Action switch
            {
                "claim" => AhelpAction.Claim,
                "resolve" => AhelpAction.Resolve,
                "reject" => AhelpAction.Reject,
                "close" => AhelpAction.Close,
                _ => (AhelpAction?) null,
            };
            if (action == null)
                return new { error = "invalid_request" };
            result = await Task.Run(() => Store.Change(ticket.Id, uid, name, action.Value, request.Text.Trim(), origin, Now));
        }
        if (generation != Volatile.Read(ref _roundGeneration)) return new { error = "round_changed" };
        Publish(result);
        if (result.Error.Length > 0)
            Log.Warning($"Ahelp Discord action={request.Action} ticket={ticket.Number} request={operation} rejected: {result.Error}");
        var updated = await Task.Run(() => Store.Get(request.Ticket));
        return new { error = result.Error, ticket = updated };
    }
}
