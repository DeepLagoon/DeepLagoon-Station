using System.Linq;
using System.Threading;
using Content.Server._NF.Salvage; // Frontier: graceful exped spawn failures
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Salvage.Expeditions;
using Content.Server.Salvage.Expeditions.Structure;
using Content.Shared.CCVar;
using Content.Shared._NF.CCVar; // Frontier
using Content.Shared.Cargo.Components;
using Content.Shared.Examine;
using Content.Shared.Random.Helpers;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.Audio;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.CPUJob.JobQueues.Queues;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared.Station.Components;
using Content.Shared.Coordinates;
using Content.Shared.Procedural;
using Content.Shared.Salvage;
using Robust.Shared.GameStates;
using Content.Server.Weather;
using Content.Shared.Weather;
using Robust.Shared.Random;
using Robust.Shared.Map;
using Content.Shared.Shuttles.Components; // Frontier
using Robust.Shared.Configuration; // Frontier
using Content.Server.Stack;
using Content.Shared.Stacks;
using Robust.Shared.Player;
using Robust.Shared.Map.Components;

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    /*
     * Handles setup / teardown of salvage expeditions.
     */

    private const int MissionLimit = 5;
    [Dependency] private IConfigurationManager _cfgManager = default!; // Frontier

    private readonly JobQueue _salvageQueue = new();
    private readonly List<(SpawnSalvageMissionJob Job, CancellationTokenSource CancelToken)> _salvageJobs = new();
    private readonly List<DifficultyRating> _missionDifficulties = [DifficultyRating.Moderate, DifficultyRating.Hazardous, DifficultyRating.Extreme]; // Frontier
    private const double SalvageJobTime = 0.002;

    private float _cooldown;
    private float _failedCooldown;

    private void InitializeExpeditions()
    {
        SubscribeLocalEvent<SalvageExpeditionConsoleComponent, ComponentInit>(OnSalvageConsoleInit);
        SubscribeLocalEvent<SalvageExpeditionConsoleComponent, EntParentChangedMessage>(OnSalvageConsoleParent);
        SubscribeLocalEvent<SalvageExpeditionConsoleComponent, ClaimSalvageMessage>(OnSalvageClaimMessage);
        SubscribeLocalEvent<SalvageExpeditionDataComponent, ExpeditionSpawnCompleteEvent>(OnExpeditionSpawnComplete); // Frontier: more gracefully handle expedition generation failures
        SubscribeLocalEvent<SalvageExpeditionConsoleComponent, FinishSalvageMessage>(OnSalvageFinishMessage); // Frontier: For early finish

        SubscribeLocalEvent<SalvageExpeditionComponent, MapInitEvent>(OnExpeditionMapInit);
        // SubscribeLocalEvent<SalvageExpeditionDataComponent, EntityUnpausedEvent>(OnDataUnpaused); // Frontier

        SubscribeLocalEvent<SalvageExpeditionComponent, ComponentShutdown>(OnExpeditionShutdown);
        // SubscribeLocalEvent<SalvageExpeditionComponent, EntityUnpausedEvent>(OnExpeditionUnpaused); // Frontier
        SubscribeLocalEvent<SalvageExpeditionComponent, ComponentGetState>(OnExpeditionGetState);

        SubscribeLocalEvent<SalvageStructureComponent, ExaminedEvent>(OnStructureExamine);

        Subs.CVar(_cfgManager, CCVars.SalvageExpeditionCooldown, SetCooldownChange, true); // Frontier
        Subs.CVar(_cfgManager, NFCCVars.SalvageExpeditionFailedCooldown, SetFailedCooldownChange, true); // Frontier
    }

    private void OnExpeditionGetState(EntityUid uid, SalvageExpeditionComponent component, ref ComponentGetState args)
    {
        args.State = new SalvageExpeditionComponentState()
        {
            Stage = component.Stage,
            EndTime = component.EndTime,
            ObjectivesTotal = component.ObjectivesTotal,
            ObjectivesCompleted = component.ObjectivesCompleted,
            RewardAmount = GetExpeditionRewardAmount(component),
            DispatchedFromOutpost = component.DispatchedFromOutpost,
        };
    }

    // Frontier
    private void ShutdownExpeditions()
    {
        _cfgManager.UnsubValueChanged(CCVars.SalvageExpeditionCooldown, SetCooldownChange);
        _cfgManager.UnsubValueChanged(NFCCVars.SalvageExpeditionFailedCooldown, SetFailedCooldownChange);
    }
    // End Frontier

    private void SetCooldownChange(float obj)
    {
        // Update the active cooldowns if we change it.
        var diff = obj - _cooldown;

        var query = AllEntityQuery<SalvageExpeditionDataComponent>();

        while (query.MoveNext(out var comp))
        {
            comp.NextOffer += TimeSpan.FromSeconds(diff);
        }

        _cooldown = obj;
    }

    private void SetFailedCooldownChange(float obj)
    {
        var diff = obj - _failedCooldown;

        var query = AllEntityQuery<SalvageExpeditionDataComponent>();

        while (query.MoveNext(out var comp))
        {
            comp.NextOffer += TimeSpan.FromSeconds(diff);
        }

        _failedCooldown = obj;
    }

    private void OnExpeditionMapInit(EntityUid uid, SalvageExpeditionComponent component, MapInitEvent args)
    {
        component.SelectedSong = _audio.ResolveSound(component.Sound);
    }

    private void OnExpeditionShutdown(EntityUid uid, SalvageExpeditionComponent component, ComponentShutdown args)
    {
        component.Stream = _audio.Stop(component.Stream);
        if (component.DispatchDisk is { } disk)
            QueueDel(disk);

        foreach (var (job, cancelToken) in _salvageJobs.ToArray())
        {
            if (job.Station == component.Station)
            {
                cancelToken.Cancel();
                _salvageJobs.Remove((job, cancelToken));
            }
        }

        if (Deleted(component.Station))
            return;

        // Finish mission
        if (TryComp<SalvageExpeditionDataComponent>(component.Station, out var data))
        {
            FinishExpedition(data, uid, component, component.Station); // Frontier: null<component.Station
        }
    }

    private void OnDataUnpaused(EntityUid uid, SalvageExpeditionDataComponent component, ref EntityUnpausedEvent args)
    {
        component.NextOffer += args.PausedTime;
    }

    private void OnExpeditionUnpaused(EntityUid uid, SalvageExpeditionComponent component, ref EntityUnpausedEvent args)
    {
        component.EndTime += args.PausedTime;
    }

    private void UpdateExpeditions()
    {
        var currentTime = _timing.CurTime;
        _salvageQueue.Process();

        foreach (var (job, cancelToken) in _salvageJobs.ToArray())
        {
            switch (job.Status)
            {
                case JobStatus.Finished:
                    _salvageJobs.Remove((job, cancelToken));
                    break;
            }
        }

        var query = EntityQueryEnumerator<SalvageExpeditionDataComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            // Update offers
            if (comp.NextOffer > currentTime || comp.Claimed)
                continue;

            if (!HasComp<FTLComponent>(_station.GetLargestGrid((uid, Comp<StationDataComponent>(uid))))) // Frontier
                comp.Cooldown = false;
            //comp.NextOffer += TimeSpan.FromSeconds(_cooldown); // Frontier
            comp.NextOffer = currentTime + TimeSpan.FromSeconds(_cooldown); // Frontier
            GenerateMissions(comp);
            UpdateConsoles(uid, comp);
        }
    }

    private void FinishExpedition(SalvageExpeditionDataComponent component, EntityUid uid, SalvageExpeditionComponent expedition, EntityUid? shuttle)
    {
        // Finish mission cleanup.
        switch (expedition.MissionParams.MissionType)
        {
            // Handles the mining taxation.
            case SalvageMissionType.Mining:
                expedition.Completed = !expedition.DispatchedFromOutpost ||
                                       expedition.Stage >= ExpeditionStage.Running;

                if (shuttle != null && TryComp<SalvageMiningExpeditionComponent>(uid, out var mining))
                {
                    var xformQuery = GetEntityQuery<TransformComponent>();
                    var entities = new List<EntityUid>();
                    MiningTax(entities, shuttle.Value, mining, xformQuery);

                    var tax = GetMiningTax(expedition.MissionParams.Difficulty);
                    _random.Shuffle(entities);

                    // TODO: urgh this pr is already taking so long I'll do this later
                    for (var i = 0; i < Math.Ceiling(entities.Count * tax); i++)
                    {
                        // QueueDel(entities[i]);
                    }
                }

                break;
        }

        // Handle payout after expedition has finished
        if (expedition.Completed)
        {
            Log.Debug($"Completed mission {expedition.MissionParams.MissionType} with seed {expedition.MissionParams.Seed}");
            component.NextOffer = _timing.CurTime + TimeSpan.FromSeconds(_cooldown);
            Announce(uid, Loc.GetString("salvage-expedition-mission-completed"));
            GiveRewards(uid, expedition);
        }
        else
        {
            Log.Debug($"Failed mission {expedition.MissionParams.MissionType} with seed {expedition.MissionParams.Seed}");
            component.NextOffer = _timing.CurTime + TimeSpan.FromSeconds(_failedCooldown);
            Announce(uid, Loc.GetString("salvage-expedition-mission-failed"));
        }


        component.ActiveMission = 0;
        component.CanFinish = false;
        component.Cooldown = true;
        if (shuttle != null) // Frontier
            UpdateConsoles(shuttle.Value, component); // Frontier
    }

    /// <summary>
    /// Deducts ore tax for mining.
    /// </summary>
    private void MiningTax(List<EntityUid> entities, EntityUid entity, SalvageMiningExpeditionComponent mining, EntityQuery<TransformComponent> xformQuery)
    {
        if (!mining.ExemptEntities.Contains(entity))
        {
            entities.Add(entity);
        }

        var xform = xformQuery.GetComponent(entity);
        var children = xform.ChildEnumerator;

        while (children.MoveNext(out var child))
        {
            MiningTax(entities, child, mining, xformQuery);
        }
    }

    private void GenerateMissions(SalvageExpeditionDataComponent component)
    {
        component.Missions.Clear();
        var configs = Enum.GetValues<SalvageMissionType>().ToList();

        // Temporarily removed coz it SUCKS
        configs.Remove(SalvageMissionType.Mining);

        // this doesn't support having more missions than types of ratings
        // but the previous system didn't do that either.
        var allDifficulties = _missionDifficulties; // Frontier: Enum.GetValues<DifficultyRating>() < _missionDifficulties
        _random.Shuffle(allDifficulties);
        var difficulties = allDifficulties.Take(MissionLimit).ToList();
        // difficulties.Sort(); // Frontier: sort later

        // Frontier: multiple missions per difficulty
        // If we support more missions than there are accepted types, pick more until you're up to MissionLimit
        while (difficulties.Count < MissionLimit)
        {
            var difficultyIndex = _random.Next(_missionDifficulties.Count);
            difficulties.Add(_missionDifficulties[difficultyIndex]);
        }
        difficulties.Sort();
        // End Frontier: multiple missions per difficulty

        if (configs.Count == 0)
            return;

        for (var i = 0; i < MissionLimit; i++)
        {
            _random.Shuffle(configs);
            var rating = difficulties[i];

            foreach (var config in configs)
            {
                var mission = new SalvageMissionParams
                {
                    Index = component.NextIndex,
                    MissionType = config,
                    Seed = _random.Next(),
                    Difficulty = rating,
                };

                component.Missions[component.NextIndex++] = mission;
                break;
            }
        }
    }

    private SalvageExpeditionConsoleState GetState(SalvageExpeditionDataComponent component)
    {
        var missions = component.Missions.Values.ToList();
        //return new SalvageExpeditionConsoleState(component.NextOffer, component.Claimed, component.Cooldown, component.ActiveMission, missions);
        return new SalvageExpeditionConsoleState(component.NextOffer, component.Claimed, component.Cooldown, component.CanFinish, component.ActiveMission, missions); // Frontier
    }

    private void SpawnMission(SalvageMissionParams missionParams, EntityUid station, EntityUid dispatchConsole, EntityUid? coordinatesDisk)
    {
        var cancelToken = new CancellationTokenSource();
        var job = new SpawnSalvageMissionJob(
            SalvageJobTime,
            EntityManager,
            _timing,
            _mapManager,
            _prototypeManager,
            _anchorable,
            _biome,
            _weather,
            _dungeon,
            _shuttle,
            _station,
            _metaData,
            this,
            _transform,
            _mapSystem,
            station,
            dispatchConsole,
            coordinatesDisk,
            missionParams,
            cancelToken.Token);

        _salvageJobs.Add((job, cancelToken));
        _salvageQueue.EnqueueJob(job);
    }

    private void OnStructureExamine(EntityUid uid, SalvageStructureComponent component, ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("salvage-expedition-structure-examine"));
    }

    private int GetExpeditionRewardAmount(SalvageExpeditionComponent expedition)
    {
        if (expedition.DispatchedFromOutpost)
        {
            return expedition.Difficulty switch
            {
                DifficultyRating.Extreme => 15,
                DifficultyRating.Hazardous => 10,
                _ => 5,
            };
        }

        var amount = 0;
        foreach (var reward in expedition.Rewards)
        {
            if (_prototypeManager.Index(reward).TryGetComponent<StaticPriceComponent>(out var price))
                amount += (int) price.Price;
        }
        return amount;
    }

    private EntityUid? FindRewardConsole(SalvageExpeditionComponent expedition)
    {
        bool Matches(EntityUid uid, SalvageExpeditionConsoleComponent console) =>
            !TerminatingOrDeleted(uid) &&
            (!expedition.DispatchedFromOutpost || console.DispatchByDisk) &&
            GetExpeditionSource(uid, console) == expedition.Station &&
            Transform(uid).MapUid is { } map && !TerminatingOrDeleted(map);

        if (expedition.DispatchConsole is { } dispatch && TryComp<SalvageExpeditionConsoleComponent>(dispatch, out var console)
            && Matches(dispatch, console))
            return dispatch;

        var consoles = AllEntityQuery<SalvageExpeditionConsoleComponent>();
        while (consoles.MoveNext(out var uid, out var candidate))
        {
            if (Matches(uid, candidate))
                return uid;
        }
        return null;
    }

    private void GiveRewards(EntityUid expedition, SalvageExpeditionComponent comp)
    {
        if (FindRewardConsole(comp) is not { } console)
            return;

        var destination = Transform(console).Coordinates;
        var issued = 0;
        if (comp.DispatchedFromOutpost)
        {
            var crystal = Spawn("Telecrystal", destination);
            var stack = Comp<StackComponent>(crystal);
            EntityManager.System<StackSystem>().SetCount(crystal, GetExpeditionRewardAmount(comp), stack);
            issued = stack.Count;
        }
        else if (_cfgManager.GetCVar(NFCCVars.SalvageExpeditionRewardsEnabled))
        {
            foreach (var reward in comp.Rewards)
            {
                var spawned = Spawn(reward, destination);
                if (TryComp<StaticPriceComponent>(spawned, out var price))
                    issued += (int) price.Price;
            }
        }

        if (issued > 0)
        {
            // The last shuttle has usually already entered hyperspace when the expedition map shuts down.
            var recipients = Filter.BroadcastMap(Comp<MapComponent>(expedition).MapId).SendReliably();
            foreach (var shuttle in comp.ReturnLocations.Keys)
                recipients.AddInGrid(shuttle);

            RaiseNetworkEvent(new SalvageExpeditionRewardIssuedEvent(issued, comp.DispatchedFromOutpost), recipients);
        }

        EntityManager.System<Content.Server._DeepLagoon.Salvage.ExpeditionLootSystem>().GiveCompletionReward(expedition, destination);
    }

    // Frontier: handle exped spawn job failures gracefully - reset the console
    private void OnExpeditionSpawnComplete(EntityUid uid, SalvageExpeditionDataComponent component, ExpeditionSpawnCompleteEvent ev)
    {
        if (component.ActiveMission == ev.MissionIndex && !ev.Success)
        {
            component.ActiveMission = 0;
            component.Cooldown = false;
            UpdateConsoles(uid, component);
        }
    }
    // End Frontier
}
