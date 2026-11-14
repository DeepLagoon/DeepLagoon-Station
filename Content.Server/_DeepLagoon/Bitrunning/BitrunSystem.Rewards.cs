using System.Linq;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Research.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunCacheComponent : Component
{
    public EntityUid? LastPuller;
    public bool DeliveryQueued;
}

public sealed partial class BitrunSystem
{
    private const int MaxReward = 150_000;

    [Dependency] private ISharedPlayerManager _rewardPlayers = default!;

    private readonly Dictionary<NetUserId, int> _personalResearchPoints = new();

    internal int GetPersonalResearchPoints(NetUserId user) => _personalResearchPoints.GetValueOrDefault(user);

    private void InitializeRewards()
    {
        SubscribeLocalEvent<BitrunCacheComponent, PullStartedMessage>(OnCachePulled);
        SubscribeLocalEvent<BitrunCacheComponent, MoveEvent>(OnCacheMoved);
    }

    private static void OnCachePulled(EntityUid uid, BitrunCacheComponent component, PullStartedMessage args)
    {
        if (uid == args.PulledUid)
            component.LastPuller = args.PullerUid;
    }

    private void OnCacheMoved(Entity<BitrunCacheComponent> cache, ref MoveEvent args)
    {
        if (!args.OnlyRotation && args.Component.MapUid is { } map && _domains.TryGetValue(map, out var run))
            CheckCacheDelivery(run, cache, args.Component);
    }

    private void CheckCacheDelivery(Run run, Entity<BitrunCacheComponent> cache, TransformComponent xform)
    {
        if (run.Completed || run.StopAt != null || run.Cache != cache.Owner || cache.Comp.DeliveryQueued ||
            xform.MapUid != run.Map || xform.GridUid is not { } gridUid ||
            !TryComp<MapGridComponent>(gridUid, out var grid) ||
            _containers.IsEntityInContainer(cache) || cache.Comp.LastPuller is not { } puller ||
            !run.Avatars.Contains(puller) || !TryComp<BitrunAvatarComponent>(puller, out var avatar) ||
            avatar.Side != BitrunSide.Attackers || Transform(puller).GridUid != gridUid)
            return;

        var activelyPulling = TryComp<PullerComponent>(puller, out var pulling) && pulling.Pulling == cache.Owner;
        if (!activelyPulling && !IsNearby(cache, puller, 2f))
            return;

        var tile = _maps.CoordinatesToTile(gridUid, grid, xform.Coordinates);
        foreach (var goal in run.Goals)
        {
            if (TerminatingOrDeleted(goal))
                continue;

            var goalTransform = Transform(goal);
            if (goalTransform.GridUid != gridUid ||
                _maps.CoordinatesToTile(gridUid, grid, goalTransform.Coordinates) != tile)
                continue;

            // Record the arrival during movement and finish the run outside physics.
            cache.Comp.DeliveryQueued = true;
            return;
        }
    }

    private void CompleteRun(Run run, BitrunSide winner, bool elimination = false)
    {
        if (run.Completed)
            return;

        var winners = GetWinningUsers(run, winner).OrderBy(user => user.UserId).ToArray();
        if (winners.Length == 0)
        {
            DrawRun(run);
            return;
        }

        MarkResult(run);
        var credits = Math.Max(0, run.Domain.Credits);
        if (TryComp<BitrunServerComponent>(run.Server, out var server))
            server.Credits = AddPoints(server.Credits, credits + (run.Random ? 1 : 0));

        var points = Math.Clamp(run.Domain.Reward, 0, MaxReward);
        var teamCredits = credits * 100;
        var side = WinnerName(winner);
        NotifyAvatars(run, Loc.GetString("bitrun-pvp-finished", ("side", side)));
        for (var index = 0; index < winners.Length; index++)
        {
            var user = winners[index];
            var share = TeamShare(points, winners.Length, index);
            _personalResearchPoints[user] = AddPoints(_personalResearchPoints.GetValueOrDefault(user), share);
            _personalCredits[user] = AddPoints(_personalCredits.GetValueOrDefault(user),
                TeamShare(teamCredits, winners.Length, index));

            if (_rewardPlayers.TryGetSessionById(user, out var session) && session.AttachedEntity is { } player)
                _popup.PopupEntity(Loc.GetString("bitrun-pvp-personal", ("points", share)), player, player);
        }

        var time = _timing.CurTime - run.Started;
        var grade = GradeRun(run, time);
        IncreaseGlitchThreat(run, grade);
        EntProtoId[] loot = elimination ? [] : GetRewardLoot(run, grade);
        var payout = new Payout(run, winner, points, grade, (int) time.TotalSeconds, loot);
        _payouts.Add(run.Id, payout);
        ProcessPayout(payout);
    }

    private void DrawRun(Run run)
    {
        if (run.Completed)
            return;

        MarkResult(run);
        var result = Loc.GetString("bitrun-pvp-draw", ("domain", Loc.GetString(run.Domain.Name)));
        _results[run.Server] = result;
        NotifyAvatars(run, result);
    }

    private void MarkResult(Run run)
    {
        run.Completed = true;
        run.StopAt = _timing.CurTime + ResultGrace;
        if (run.Cache is { } cache)
            QueueDel(cache);
        run.Cache = null;
    }

    private static int TeamShare(int total, int members, int index) =>
        total / members + (index < total % members ? 1 : 0);

    private static int AddPoints(int balance, int points) =>
        Math.Min(int.MaxValue - points, balance) + points;

    private string WinnerName(BitrunSide side) =>
        Loc.GetString(side == BitrunSide.Attackers ? "bitrun-side-attackers" : "bitrun-side-defenders");

    private void ProcessPayout(Payout payout)
    {
        if (!payout.PointsPaid && Exists(payout.Receiver) &&
            !TerminatingOrDeleted(payout.Receiver) && IsOnline(payout.Receiver) &&
            TryComp<ResearchServerComponent>(payout.Receiver, out var research) &&
            research.Points <= int.MaxValue - payout.Points)
        {
            payout.PointsPaid = true;
            _research.ModifyServerPoints(payout.Receiver, payout.Points, research);
            Log.Info($"Bitrun {payout.Id} ({payout.Domain.ID}) paid {payout.Points} points to {payout.Receiver}");
        }
        else if (!payout.PointsPaid && !payout.ReceiverLost && TerminatingOrDeleted(payout.Receiver))
        {
            payout.ReceiverLost = true;
            Log.Warning($"Bitrun {payout.Id} ({payout.Domain.ID}) could not pay {payout.Points}: R&D {payout.Receiver} was removed");
        }

        if (!payout.LootPaid && IsOnline(payout.Server) &&
            FindEquipment<BitrunForgeComponent>(payout.Server, EquipmentRange) is { } forge)
        {
            var output = Spawn("BitrunDecryptedCache", Transform(forge).Coordinates);
            FillReward(output, payout.Loot);
            payout.LootPaid = true;
        }

        var status = payout.PointsPaid ? "bitrun-payment-paid"
            : payout.ReceiverLost ? "bitrun-payment-lost" : "bitrun-payment-pending";
        if (_latestRuns.TryGetValue(payout.Server, out var latest) && latest == payout.Id)
        {
            _results[payout.Server] = Loc.GetString("bitrun-pvp-result",
                ("domain", Loc.GetString(payout.Domain.Name)),
                ("side", WinnerName(payout.Winner)),
                ("grade", payout.Grade),
                ("seconds", payout.Seconds),
                ("points", payout.Points),
                ("status", Loc.GetString(status)),
                ("loot", Loc.GetString(payout.Loot.Length == 0 ? "bitrun-pvp-no-loot" : payout.LootPaid ? "bitrun-loot-paid" : "bitrun-loot-pending")));
        }

        if ((payout.PointsPaid || payout.ReceiverLost) && payout.LootPaid || !Exists(payout.Server) && (payout.PointsPaid || payout.ReceiverLost))
            _payouts.Remove(payout.Id);
    }

    private sealed class Payout(Run run, BitrunSide winner, int points, string grade, int seconds, EntProtoId[] loot)
    {
        public readonly Guid Id = run.Id;
        public readonly EntityUid Server = run.Server;
        public readonly EntityUid Receiver = run.Receiver;
        public readonly BitrunSide Winner = winner;
        public readonly BitrunDomainPrototype Domain = run.Domain;
        public readonly int Points = points;
        public readonly string Grade = grade;
        public readonly int Seconds = seconds;
        public readonly EntProtoId[] Loot = loot;
        public bool PointsPaid = points == 0;
        public bool ReceiverLost;
        public bool LootPaid = loot.Length == 0;
    }
}
