using System.Linq;
using Content.Server.GameTicking.Events;
using Content.Server.Station.Components;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Clothing;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.GameTicking;

public sealed partial class GameTicker
{
    private sealed record EquipmentReview(int Request, int Round, int CharacterSlot, EntityUid Station, string Job, bool LateJoin);
    private readonly Dictionary<NetUserId, EquipmentReview> _startingEquipmentReviews = new();
    private int _nextEquipmentReview;

    private bool RequestStartingEquipmentReview(ICommonSession player, HumanoidCharacterProfile profile, EntityUid station,
        JobPrototype job, bool lateJoin)
    {
        if (!LobbyEnabled || job.JobEntity != null || job.ID == PreferredEquipmentJob(profile) ||
            !_prefsManager.TryGetCachedPreferences(player.UserId, out var prefs))
            return false;

        var review = new EquipmentReview(++_nextEquipmentReview, RoundId, prefs.SelectedCharacterIndex, station, job.ID, lateJoin);
        _startingEquipmentReviews[player.UserId] = review;
        RaiseNetworkEvent(new StartingEquipmentReviewEvent(review.Request, review.Job, review.CharacterSlot), player.Channel);
        return true;
    }

    private async void OnStartingEquipmentReviewReply(StartingEquipmentReviewReply reply, EntitySessionEventArgs args)
    {
        var player = args.SenderSession;
        if (!_startingEquipmentReviews.TryGetValue(player.UserId, out var review) || review.Request != reply.Request)
            return;

        _startingEquipmentReviews.Remove(player.UserId);
        if (!reply.Join || !TryResolveEquipmentReview(player, review, out var profile, out var job))
            return;

        if (reply.Job != null || reply.Personal != null)
        {
            profile = ApplyEquipmentReview(player, profile, job, reply);
            try
            {
                await _prefsManager.SetProfile(player.UserId, review.CharacterSlot, profile);
            }
            catch (Exception e)
            {
                Log.Error($"Failed to save starting equipment for {player.UserId}: {e}");
                _chatManager.DispatchServerMessage(player, Loc.GetString("starting-equipment-review-save-failed"));
                return;
            }

            if (!TryResolveEquipmentReview(player, review, out profile, out job))
                return;
        }

        CompletePlayerSpawn(player, profile, review.Station, job, review.LateJoin, silent: false);
        if (reply.Job != null || reply.Personal != null)
            _prefsManager.SendPreferences(player);
    }

    private bool TryResolveEquipmentReview(ICommonSession player, EquipmentReview review,
        out HumanoidCharacterProfile profile, out JobPrototype job)
    {
        profile = default!;
        job = default!;
        if (review.Round != RoundId || RunLevel != GameRunLevel.InRound || player.Status != SessionStatus.InGame ||
            !CheckDiscordRoundAdmission(player) ||
            !_playerGameStatuses.TryGetValue(player.UserId, out var status) || status == PlayerGameStatus.JoinedGame ||
            _startingEquipmentReviews.ContainsKey(player.UserId) || !_userDb.IsLoadComplete(player))
            return false;

        if (!_prefsManager.TryGetCachedPreferences(player.UserId, out var prefs) ||
            prefs.SelectedCharacterIndex != review.CharacterSlot || prefs.SelectedCharacter is not HumanoidCharacterProfile selected ||
            !_prototypeManager.TryIndex<JobPrototype>(review.Job, out var assigned) ||
            TerminatingOrDeleted(review.Station) || !HasComp<StationSpawningComponent>(review.Station) ||
            !TryComp<StationJobsComponent>(review.Station, out var jobs) ||
            !_stationJobs.TryGetJobSlot(review.Station, review.Job, out var slots, jobs) || slots == 0 ||
            review.LateJoin && DisallowLateJoin || _banManager.GetJobBans(player.UserId) is not { } bans || bans.Contains(review.Job))
        {
            _chatManager.DispatchServerMessage(player, Loc.GetString("starting-equipment-review-unavailable"));
            return false;
        }

        var allowed = new IsJobAllowedEvent(player, new ProtoId<JobPrototype>(review.Job));
        RaiseLocalEvent(ref allowed);
        if (allowed.Cancelled || !CanJoinFaction(player, review.Job))
            return false;
        var restrictions = new GetDisallowedJobsEvent(player, new HashSet<ProtoId<JobPrototype>>());
        RaiseLocalEvent(ref restrictions);
        if (restrictions.Jobs.Contains(review.Job))
            return false;

        profile = selected;
        job = assigned;
        return true;
    }

    private HumanoidCharacterProfile ApplyEquipmentReview(ICommonSession player, HumanoidCharacterProfile profile, JobPrototype job,
        StartingEquipmentReviewReply reply)
    {
        var roleId = LoadoutSystem.GetJobPrototype(job.ID);
        if (reply.Job is { } jobChanges && _prototypeManager.HasIndex<RoleLoadoutPrototype>(roleId))
        {
            var current = profile.GetLoadoutOrDefault(roleId, player, profile.Species, EntityManager, _prototypeManager).Clone();
            MergeBackpackSelections(current, jobChanges, personal: false);
            current.BackpackLayout = jobChanges.BackpackLayout;
            profile = profile.WithLoadout(current);
        }

        if (reply.Personal is { } personalChanges)
        {
            var current = profile.Loadouts.TryGetValue(PersonalLoadoutSystem.Role, out var saved)
                ? saved.Clone() : new RoleLoadout(PersonalLoadoutSystem.Role);
            MergeBackpackSelections(current, personalChanges, personal: true);
            if (personalChanges.BackpackLayout.GetValueOrDefault(SharedStationSpawningSystem.DefaultSurvivalKit).Removed)
                current.BackpackLayout[SharedStationSpawningSystem.DefaultSurvivalKit] = new BackpackItemPlacement(true, 0, 0, 0);
            else
                current.BackpackLayout.Remove(SharedStationSpawningSystem.DefaultSurvivalKit);
            profile = profile.WithLoadout(current);
        }
        return profile;
    }

    private void MergeBackpackSelections(RoleLoadout current, RoleLoadout changes, bool personal)
    {
        var equipment = EntityManager.System<PersonalLoadoutEquipSystem>();
        foreach (var groupId in _prototypeManager.Index(current.Role).Groups)
        {
            if (!changes.SelectedLoadouts.TryGetValue(groupId, out var proposed))
                continue;

            var group = _prototypeManager.Index(groupId);
            if (group.Hidden)
                continue;
            var kept = current.SelectedLoadouts.TryGetValue(groupId, out var saved)
                ? saved.Where(item => !Editable(item.Prototype)).ToList() : new List<Loadout>();
            var available = group.Loadouts.Concat(group.Subgroups.SelectMany(id => _prototypeManager.Index(id).Loadouts)).ToHashSet();
            kept.AddRange(proposed.Where(item => available.Contains(item.Prototype) && Editable(item.Prototype))
                .DistinctBy(item => item.Prototype).Take(Math.Max(0, group.MaxLimit - kept.Count)));
            current.SelectedLoadouts[groupId] = kept;
        }

        bool Editable(ProtoId<LoadoutPrototype> id) => _prototypeManager.TryIndex(id, out var item) && (personal
            ? item.PersonalItems.Count > 0 && Enumerable.Range(0, item.PersonalItems.Count).All(i => equipment.IsBackpackItem(item, i))
            : SharedStationSpawningSystem.IsBackpackLoadout(item, _prototypeManager));
    }
}
