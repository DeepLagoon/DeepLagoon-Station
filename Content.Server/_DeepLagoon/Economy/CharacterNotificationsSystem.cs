using System.Linq;
using System.Threading.Tasks;
using Content.Server.CartridgeLoader;
using Content.Server.Database;
using Content.Server._NF.Bank;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared.CartridgeLoader;
using Content.Shared.PDA;
using Robust.Server.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Economy;

[RegisterComponent]
public sealed partial class CharacterNotificationsCartridgeComponent : Component;

/// <summary>Private character history. Personal snapshots are sent to a session, never cached on a PDA entity.</summary>
public sealed partial class CharacterNotificationsSystem : EntitySystem
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private CartridgeLoaderSystem _loader = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<(Guid User, int Profile), HashSet<string>> _sent = new();
    private readonly Dictionary<Guid, TimeSpan> _requests = new();
    private TimeSpan _next;
    private bool _polling;
    public override void Initialize()
    {
        SubscribeNetworkEvent<CharacterNotificationsRefresh>(OnRequest);
    }
    public bool CanRead(Robust.Shared.Player.ICommonSession session, EntityUid pda)
        => session.AttachedEntity is { } actor && Exists(pda) && Transform(pda).ParentUid == actor && HasComp<PdaComponent>(pda) &&
            _ui.IsUiOpen(pda, PdaUiKey.Key, actor) && TryComp<CartridgeLoaderComponent>(pda, out var loader) &&
            loader.ActiveProgram is { } program && HasComp<CharacterNotificationsCartridgeComponent>(program);
    private async void OnRequest(CharacterNotificationsRefresh request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!TryGetEntity(request.Pda, out var pda) || pda is not { } device || !CanRead(session, device)) return;
        if (_requests.TryGetValue(session.UserId.UserId, out var next) && _timing.RealTime < next) return;
        _requests[session.UserId.UserId] = _timing.RealTime + TimeSpan.FromSeconds(1);
        try
        {
            if (!_bank.TryGetCharacter(session, out _, out _, out var slot)) return;
            var actor = session.AttachedEntity!.Value;
            var account = await _db.ReadEconomyAccountAsync(session.UserId.UserId, slot);
            if (session.AttachedEntity != actor || !CanRead(session, device) || !_bank.IsCharacter(session, slot)) return;
            var notices = await _db.ReadCharacterNotificationsAsync(session.UserId.UserId, account.Profile, request.MarkRead);
            if (session.AttachedEntity != actor || !CanRead(session, device) || !_bank.IsCharacter(session, slot)) return;
            RaiseNetworkEvent(new CharacterNotificationsSnapshot(request.Pda, GetNetEntity(actor), notices.Select(n =>
                new CharacterNoticeView(n.Id, n.Created, n.Title, n.Text, n.Amount, n.Read)).ToArray()), session);
        }
        catch (Exception e) { Log.Warning($"Character notification history deferred: {e.Message}"); }
    }
    public override void Update(float frameTime)
    {
        if (_polling || _timing.RealTime < _next) return;
        _next = _timing.RealTime + TimeSpan.FromSeconds(10);
        _ = Poll();
    }
    private async Task Poll()
    {
        _polling = true;
        try
        {
            foreach (var session in _players.Sessions.ToArray())
            {
                if (session.AttachedEntity is not { } actor || !_bank.TryGetCharacter(session, out _, out _, out var slot)) continue;
                var devices = EntityQuery<PdaComponent, TransformComponent>().Where(p => p.Item2.ParentUid == actor).Select(p => p.Item1.Owner).ToArray();
                if (devices.Length == 0) continue;
                var account = await _db.ReadEconomyAccountAsync(session.UserId.UserId, slot);
                var notices = await _db.ReadCharacterNotificationsAsync(session.UserId.UserId, account.Profile, false);
                if (session.AttachedEntity != actor || !_bank.IsCharacter(session, slot)) continue;
                var key = (session.UserId.UserId, account.Profile);
                if (!_sent.TryGetValue(key, out var sent)) _sent[key] = sent = new();
                var fresh = notices.Where(n => !n.Read && !sent.Contains(n.Id)).ToArray();
                if (fresh.Length > 0 && Exists(devices[0]) && Transform(devices[0]).ParentUid == actor)
                    _loader.SendNotification(devices[0], fresh[0].Title,
                        fresh.Length == 1 ? fresh[0].Text : $"Новых уведомлений: {fresh.Length}. Откройте «Уведомления».");
                sent.IntersectWith(notices.Select(n => n.Id));
                sent.UnionWith(notices.Select(n => n.Id));
            }
            if (_sent.Count > 256) _sent.Clear();
        }
        catch (Exception e) { Log.Warning($"PDA notification delivery deferred: {e.Message}"); }
        finally { _polling = false; }
    }
}
