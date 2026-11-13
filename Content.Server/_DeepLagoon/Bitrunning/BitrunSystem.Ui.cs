using System.Linq;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Interaction;
using Content.Shared.Research.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Bitrunning;

public sealed partial class BitrunSystem
{
    private static readonly string[] Loadouts = ["explorer", "medic", "security"];

    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;

    private readonly Dictionary<NetUserId, int> _personalCredits = new();

    private void InitializeUi()
    {
        SubscribeLocalEvent<BitrunConsoleComponent, BoundUIOpenedEvent>(OnConsoleOpened);
        SubscribeLocalEvent<BitrunConsoleComponent, BitrunUiMessage>(OnConsoleMessage);
        SubscribeLocalEvent<BitrunPodComponent, BoundUIOpenedEvent>(OnPodOpened);
        SubscribeLocalEvent<BitrunPodComponent, BitrunUiMessage>(OnPodMessage);
        SubscribeLocalEvent<BitrunVendorComponent, BoundUIOpenedEvent>(OnVendorOpened);
        SubscribeLocalEvent<BitrunVendorComponent, BitrunUiMessage>(OnVendorMessage);
    }

    private void OnConsoleOpened(EntityUid uid, BitrunConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (args.UiKey is BitrunUiKey.Key)
            RefreshConsole(uid);
    }

    private void OnConsoleMessage(EntityUid uid, BitrunConsoleComponent component, BitrunUiMessage args)
    {
        if (!CanOperate(uid, args.Actor))
            return;

        if (args.Action == BitrunUiAction.BindResearch)
        {
            if (args.Target is { } target && TryGetEntity(target, out var receiver) &&
                CanUseResearch(uid, receiver.Value))
                _research.RegisterClient(uid, receiver.Value);

            RefreshConsole(uid);
            return;
        }

        if (FindEquipment<BitrunServerComponent>(uid, 1.01f) is not { } server)
            return;

        switch (args.Action)
        {
            case BitrunUiAction.Start:
            case BitrunUiAction.Random:
                if (_research.TryGetClientServer(uid, out var bound, out _) && CanUseResearch(uid, bound.Value))
                    TryLaunchFromConsole(server, bound.Value, args.Action, args.Id);
                break;
            case BitrunUiAction.Stop when _lobbies.TryGetValue(server, out var lobby) && lobby.Id == args.Session:
                CancelLobby(lobby);
                break;
            case BitrunUiAction.Stop when _runs.TryGetValue(server, out var run) && run.Id == args.Session:
                BeginStop(run);
                break;
        }

        RefreshConsole(uid);
    }

    private void TryLaunchFromConsole(EntityUid server, EntityUid receiver, BitrunUiAction action, string? id)
    {
        var machine = Comp<BitrunServerComponent>(server);
        var random = action == BitrunUiAction.Random;
        BitrunDomainPrototype? domain;
        if (random)
        {
            var available = _prototypes.EnumeratePrototypes<BitrunDomainPrototype>()
                .Where(p => !p.TestOnly && p.Cost is > 0 and < 20 && p.Cost <= machine.Credits).ToArray();
            domain = available.Length > 0 ? _random.Pick(available) : null;
        }
        else
        {
            domain = id != null && _prototypes.TryIndex<BitrunDomainPrototype>(id, out var selected) ? selected : null;
        }

        if (domain is not { TestOnly: false } || !TryScheduleRun(server, machine, domain, receiver, random))
            _results[server] = Loc.GetString("bitrun-start-failed");
    }

    private bool CanOperate(EntityUid device, EntityUid actor) =>
        IsOnline(device) && !HasComp<BitrunAvatarComponent>(actor) && _interaction.InRangeUnobstructed(actor, device);

    private void RefreshOpenConsoles()
    {
        var consoles = EntityQueryEnumerator<BitrunConsoleComponent>();
        while (consoles.MoveNext(out var uid, out _))
        {
            if (_ui.IsUiOpen(uid, BitrunUiKey.Key))
                RefreshConsole(uid);
        }

        var pods = EntityQueryEnumerator<BitrunPodComponent>();
        while (pods.MoveNext(out var uid, out var pod))
        {
            if (!_pods.ContainsKey(uid) && !_queuedPods.ContainsKey(uid) && GetPodBody(uid) is { } body &&
                (!IsOnline(uid) || !Transform(uid).Anchored || !TryGetPodUser(body, out _, out _)))
                ReleaseBody(uid, body);

            if (_ui.IsUiOpen(uid, BitrunUiKey.Key))
                RefreshPod(uid, pod);
        }
    }

    private void RefreshConsole(EntityUid uid)
    {
        var state = new BitrunUiState
        {
            Mode = BitrunUiMode.Console,
            Status = Loc.GetString("bitrun-no-server"),
            Domains = _prototypes.EnumeratePrototypes<BitrunDomainPrototype>()
                .Where(domain => !domain.TestOnly)
                .OrderBy(domain => domain.Difficulty)
                .ThenBy(domain => domain.ID)
                .Select(domain => new BitrunDomainEntry(domain.ID, Loc.GetString(domain.Name),
                    Loc.GetString(domain.Description), domain.Difficulty, domain.Cost, domain.Reward))
                .ToArray(),
            ResearchServers = GetResearchEntries(uid),
        };

        var hasReceiver = _research.TryGetClientServer(uid, out var receiver, out _) && CanUseResearch(uid, receiver.Value);
        state.ResearchServer = hasReceiver ? GetNetEntity(receiver) : null;
        if (FindEquipment<BitrunServerComponent>(uid, 1.01f) is { } server)
        {
            state.Credits = Comp<BitrunServerComponent>(server).Credits;
            state.CanStart = hasReceiver && !_lobbies.ContainsKey(server) && !_runs.ContainsKey(server);
            if (_lobbies.TryGetValue(server, out var lobby))
            {
                state.DomainId = lobby.Domain.ID;
                state.Sessions = [GetSessionEntry(server, lobby)];
                state.CanStop = true;
                state.Status = Loc.GetString("bitrun-lobby-waiting");
            }
            else if (_runs.TryGetValue(server, out var run))
            {
                state.DomainId = run.Domain.ID;
                state.Sessions = [GetSessionEntry(run)];
                state.CanStop = true;
                state.Status = run.Completed && _results.TryGetValue(server, out var result)
                    ? result
                    : GetRunStatus(run);
            }
            else
            {
                state.Status = _results.GetValueOrDefault(server)
                    ?? Loc.GetString(hasReceiver ? "bitrun-idle" : "bitrun-no-research");
            }
        }

        SetSessionUiState(uid, state);
    }

    private string GetRunStatus(Run run)
    {
        if (run.StopAt != null)
            return Loc.GetString("bitrun-stop-warning");

        if (run.Cache != null)
        {
            var forge = FindEquipment<BitrunForgeComponent>(run.Server, EquipmentRange);
            return Loc.GetString(forge == null ? "bitrun-no-forge" : "bitrun-find-cache");
        }

        return Loc.GetString("bitrun-objective-progress", ("progress", run.ObjectiveProgress),
            ("required", run.Domain.ObjectiveCount));
    }

    private BitrunResearchEntry[] GetResearchEntries(EntityUid console)
    {
        var list = new List<BitrunResearchEntry>();
        var query = EntityQueryEnumerator<ResearchServerComponent>();
        while (query.MoveNext(out var uid, out var receiver))
        {
            if (CanUseResearch(console, uid))
                list.Add(new BitrunResearchEntry(GetNetEntity(uid), receiver.ServerName));
        }

        return list.OrderBy(entry => entry.Name).ToArray();
    }

    private void OnPodOpened(EntityUid uid, BitrunPodComponent component, BoundUIOpenedEvent args)
    {
        if (args.UiKey is BitrunUiKey.Key)
            RefreshPod(uid, component);
    }

    private void OnPodMessage(EntityUid uid, BitrunPodComponent component, BitrunUiMessage args)
    {
        if (args.Action == BitrunUiAction.Eject && !_pods.ContainsKey(uid) && GetPodBody(uid) == args.Actor)
        {
            LeavePod(uid, args.Actor);
            RefreshPod(uid, component);
            return;
        }

        if (!CanOperate(uid, args.Actor))
            return;

        switch (args.Action)
        {
            case BitrunUiAction.Enter when args.Target is { } target && TryGetEntity(target, out var server) &&
                                           args.Side is BitrunSide.Attackers or BitrunSide.Defenders &&
                                           args.Session is { } session && CanJoinSession(uid, server.Value):
                if (_lobbies.TryGetValue(server.Value, out var lobby) && lobby.Id == session ||
                    _runs.TryGetValue(server.Value, out var run) && run.Id == session)
                    EnterPod(uid, component, args.Actor, server.Value, args.Side.Value);
                break;
            case BitrunUiAction.Eject:
                StartEject(uid, args.Actor);
                break;
            case BitrunUiAction.SelectLoadout when args.Id is { } loadout && Loadouts.Contains(loadout):
                if (!_pods.ContainsKey(uid) && !_queuedPods.ContainsKey(uid) &&
                    (GetPodBody(uid) is not { } occupant || occupant == args.Actor))
                    component.Loadout = loadout;
                break;
        }

        RefreshPod(uid, component);
    }

    private void RefreshPod(EntityUid uid, BitrunPodComponent pod)
    {
        var state = new BitrunUiState
        {
            Mode = BitrunUiMode.Pod,
            Loadouts = Loadouts,
            SelectedLoadout = pod.Loadout,
        };

        if (_queuedPods.TryGetValue(uid, out var lobby))
        {
            state.Sessions = [GetSessionEntry(lobby.Server, lobby)];
            state.OccupiedSide = lobby.Queued.Values.FirstOrDefault(player => player.Pod == uid)?.Side;
            state.CanStop = true;
            state.Status = Loc.GetString("bitrun-pod-queued");
        }
        else if (_pods.TryGetValue(uid, out var avatar) && TryComp<BitrunAvatarComponent>(avatar, out var link))
        {
            if (_runs.TryGetValue(link.Server, out var run))
                state.Sessions = [GetSessionEntry(run)];
            state.OccupiedSide = link.Side;
            state.CanStop = true;
            state.Status = Loc.GetString("bitrun-pod-connected");
        }
        else
        {
            state.Sessions = _lobbies.Where(pair => CanJoinSession(uid, pair.Key))
                .Select(pair => GetSessionEntry(pair.Key, pair.Value))
                .Concat(_runs.Values.Where(run => !run.Completed && run.StopAt == null && CanJoinSession(uid, run.Server))
                    .Select(GetSessionEntry))
                .OrderBy(entry => entry.Waiting)
                .ThenBy(entry => entry.Domain)
                .ThenBy(entry => entry.Id)
                .ToArray();
            if (GetPodBody(uid) is { } body)
                state.PreparingBody = GetNetEntity(body);

            state.CanStart = state.Sessions.Any(entry => entry.CanJoinAttackers || entry.CanJoinDefenders);
            state.CanStop = state.PreparingBody != null;
            state.Status = Loc.GetString(state.Sessions.Length == 0 ? "bitrun-no-domain" : "bitrun-pod-ready");
        }

        SetSessionUiState(uid, state);
    }

    private void SetSessionUiState(EntityUid device, BitrunUiState state)
    {
        if (_ui.TryGetUiState<BitrunUiState>(device, BitrunUiKey.Key, out var previous) &&
            previous.Mode == state.Mode && previous.Status == state.Status && previous.Credits == state.Credits &&
            previous.DomainId == state.DomainId && previous.ResearchServer == state.ResearchServer &&
            previous.SelectedLoadout == state.SelectedLoadout && previous.OccupiedSide == state.OccupiedSide &&
            previous.PreparingBody == state.PreparingBody &&
            previous.CanStart == state.CanStart && previous.CanStop == state.CanStop &&
            previous.Sessions.SequenceEqual(state.Sessions) && previous.Domains.SequenceEqual(state.Domains) &&
            previous.ResearchServers.SequenceEqual(state.ResearchServers))
            return;

        _ui.SetUiState(device, BitrunUiKey.Key, state);
    }

    private BitrunSessionEntry GetSessionEntry(EntityUid server, Lobby lobby) =>
        new(lobby.Id, GetNetEntity(server), Loc.GetString(lobby.Domain.Name), true, lobby.StartAt,
            lobby.Queued.Values.Count(player => player.Side == BitrunSide.Attackers),
            lobby.Queued.Values.Count(player => player.Side == BitrunSide.Defenders),
            CanEnroll(lobby, BitrunSide.Attackers), CanEnroll(lobby, BitrunSide.Defenders));

    private BitrunSessionEntry GetSessionEntry(Run run) =>
        new(run.Id, GetNetEntity(run.Server), Loc.GetString(run.Domain.Name), false, run.StopAt ?? run.EndsAt,
            GetSideCount(run, BitrunSide.Attackers), GetSideCount(run, BitrunSide.Defenders),
            CanEnroll(run, BitrunSide.Attackers), CanEnroll(run, BitrunSide.Defenders));

    private void OnVendorOpened(EntityUid uid, BitrunVendorComponent component, BoundUIOpenedEvent args)
    {
        if (args.UiKey is not BitrunUiKey.Key)
            return;

        _ui.SetUiState(uid, BitrunUiKey.Key, new BitrunUiState
        {
            Mode = BitrunUiMode.Vendor,
            ShopItems = _prototypes.EnumeratePrototypes<BitrunShopPrototype>()
                .OrderBy(item => item.Price)
                .Select(item => new BitrunShopEntry(item.ID, _prototypes.Index(item.Product).Name, item.Price))
                .ToArray(),
        });
        SendBalance(uid, args.Actor);
    }

    private void OnVendorMessage(EntityUid uid, BitrunVendorComponent component, BitrunUiMessage args)
    {
        if (!CanOperate(uid, args.Actor))
            return;

        if (args.Action == BitrunUiAction.RefreshVendorBalance)
        {
            SendBalance(uid, args.Actor);
            return;
        }

        if (args.Action != BitrunUiAction.Buy ||
            args.Id == null || !_prototypes.TryIndex<BitrunShopPrototype>(args.Id, out var item) ||
            !TryComp<ActorComponent>(args.Actor, out var actor) || item.Price <= 0)
            return;

        var user = actor.PlayerSession.UserId;
        var balance = _personalCredits.GetValueOrDefault(user);
        if (balance < item.Price)
        {
            _popup.PopupEntity(Loc.GetString("bitrun-not-enough-credits"), uid, args.Actor);
            return;
        }

        Spawn(item.Product, Transform(uid).Coordinates);
        _personalCredits[user] = balance - item.Price;
        SendBalance(uid, args.Actor);
    }

    private void SendBalance(EntityUid vendor, EntityUid actor)
    {
        if (TryComp<ActorComponent>(actor, out var player))
        {
            var balance = _personalCredits.GetValueOrDefault(player.PlayerSession.UserId);
            var researchPoints = GetPersonalResearchPoints(player.PlayerSession.UserId);
            _ui.ServerSendUiMessage(vendor, BitrunUiKey.Key, new BitrunBalanceMessage(balance, researchPoints), actor);
        }
    }
}
