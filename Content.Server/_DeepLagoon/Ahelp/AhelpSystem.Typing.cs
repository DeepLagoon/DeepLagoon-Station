using System.Linq;
using System.Threading;
using Content.Shared._DeepLagoon.Ahelp;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Ahelp;

public sealed partial class AhelpSystem
{
    private sealed record Typist(AhelpTicket Ticket, Guid Actor, string Name, bool Staff, bool Discord, TimeSpan Expires);
    private readonly Dictionary<(Guid Actor, bool Discord), Typist> _typing = new();
    private readonly Dictionary<Guid, TimeSpan> _typingRate = new();
    private readonly Dictionary<Guid, AhelpTypingRequest> _typingRequests = new();
    private TimeSpan _nextTypingPrune;

    internal static bool CanType(AhelpTicket ticket, Guid actor, bool admin, bool staff)
        => ticket.Status < AhelpStatus.Closing && (staff ? admin : ticket.Player == actor || admin);

    private async void OnTyping(AhelpTypingRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var actor = session.UserId.UserId;
        if (!Enabled || _stopping || request.Round != _roundEpoch || request.Ticket == Guid.Empty)
            return;
        if (!request.Active)
        {
            _typingRequests.Remove(actor);
            if (_typing.TryGetValue((actor, false), out var prior) && prior.Ticket.Id == request.Ticket)
                ClearTyping(actor, discord: false);
            return;
        }
        if (_typingRate.TryGetValue(actor, out var next) && _timing.RealTime < next)
            return;
        _typingRate[actor] = _timing.RealTime + TimeSpan.FromSeconds(1);
        _typingRequests[actor] = request;
        var generation = Volatile.Read(ref _roundGeneration);
        try
        {
            await _roundCleanup;
            var ticket = await System.Threading.Tasks.Task.Run(() => Store.Get(request.Ticket));
            if (generation != Volatile.Read(ref _roundGeneration) || _stopping || !session.Channel.IsConnected ||
                !_typingRequests.TryGetValue(actor, out var latest) || latest != request ||
                ticket == null || !CanType(ticket, actor, CanHelp(session), request.Staff))
                return;
            SetTyping(ticket, actor, session.Name, request.Staff, discord: false, active: true);
        }
        catch (Exception ex) { Log.Debug($"Ahelp typing update failed: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(ex)}"); }
    }

    private void SetTyping(AhelpTicket ticket, Guid actor, string name, bool staff, bool discord, bool active)
    {
        ClearTyping(actor, discord, ticket.Id);
        _typing.Remove((actor, discord));
        if (active)
            _typing[(actor, discord)] = new Typist(ticket, actor, name, staff, discord,
                _timing.RealTime + TimeSpan.FromSeconds(8));
        BroadcastTyping(ticket);
    }

    private void ClearTyping(Guid actor, bool? discord = null, Guid? keepTicket = null)
    {
        foreach (var (key, entry) in _typing.ToArray())
        {
            if (entry.Actor != actor || discord.HasValue && entry.Discord != discord.Value || entry.Ticket.Id == keepTicket)
                continue;
            _typing.Remove(key);
            BroadcastTyping(entry.Ticket);
        }
    }

    private void ClearTicketTyping(AhelpTicket ticket)
    {
        foreach (var (actor, request) in _typingRequests.ToArray())
        {
            if (request.Ticket == ticket.Id) _typingRequests.Remove(actor);
        }
        foreach (var (key, entry) in _typing.ToArray())
        {
            if (entry.Ticket.Id == ticket.Id) _typing.Remove(key);
        }
        BroadcastTyping(ticket);
    }

    private void SendTyping(ICommonSession session, AhelpTicket ticket)
    {
        var admin = CanHelp(session);
        if (!admin && session.UserId.UserId != ticket.Player) return;
        var names = _typing.Values.Where(entry => entry.Ticket.Id == ticket.Id &&
            entry.Expires > _timing.RealTime && entry.Actor != session.UserId.UserId && (admin || !entry.Staff))
            .Select(entry => entry.Name).Distinct().ToArray();
        Reply(session, new AhelpTypingState(ticket.Id, names, _roundEpoch));
    }

    private void BroadcastTyping(AhelpTicket ticket)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.UserId.UserId == ticket.Player || CanHelp(session)) SendTyping(session, ticket);
        }
    }

    private void PruneTyping()
    {
        if (_timing.RealTime < _nextTypingPrune) return;
        _nextTypingPrune = _timing.RealTime + TimeSpan.FromSeconds(1);
        foreach (var (key, entry) in _typing.ToArray())
        {
            if (Enabled && entry.Expires > _timing.RealTime) continue;
            _typing.Remove(key);
            BroadcastTyping(entry.Ticket);
        }
        foreach (var (actor, until) in _typingRate.ToArray())
        {
            if (until < _timing.RealTime) _typingRate.Remove(actor);
        }
    }
}
