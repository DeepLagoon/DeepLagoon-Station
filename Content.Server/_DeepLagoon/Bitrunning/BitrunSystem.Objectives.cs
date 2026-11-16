using System.Linq;
using Content.Server.Nutrition.EntitySystems;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunFishingRodComponent : Component;

public sealed partial class BitrunSystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    private static readonly TimeSpan IslandWaveDelay = TimeSpan.FromSeconds(8);

    private void InitializeObjectives()
    {
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, InteractHandEvent>(OnScenarioInteract);
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, InteractUsingEvent>(OnScenarioUse);
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, BitrunScenarioDoAfterEvent>(OnScenarioDoAfter);
        SubscribeLocalEvent<BitrunPodComponent, BitrunEjectDoAfterEvent>(OnEjectDoAfter);
        SubscribeLocalEvent<BitrunAvatarComponent, DrinkConsumedEvent>(OnDrinkConsumed);
    }

    private void ConfigureObjectives(Run run)
    {
        run.Scenarios.AddRange(FindMapEntities<BitrunScenarioMarkerComponent>(run.Map));
        var mobs = FindMapEntities<MobStateComponent>(run.Map);
        switch (run.Domain.Objective)
        {
            case BitrunObjective.Boss:
                foreach (var marker in run.Scenarios.Where(uid => MetaData(uid).EntityName == "boss"))
                {
                    if (mobs.FirstOrDefault(uid => MetaData(uid).EntityPrototype?.ID.StartsWith("BitrunMob", StringComparison.Ordinal) == true && IsNearby(marker, uid, 1.5f)) is { Valid: true } boss)
                        run.Targets.Add(boss);
                }
                break;

            case BitrunObjective.Hunt:
                run.Targets.UnionWith(mobs.Where(uid => MetaData(uid).EntityPrototype?.ID == "BitrunHuntAnimal"));
                break;

            case BitrunObjective.Island:
                run.IslandWaveAt = run.Started + IslandWaveDelay;
                break;

            case BitrunObjective.Stages:
                run.Targets.UnionWith(mobs.Where(uid => MetaData(uid).EntityPrototype?.ID is
                    "BitrunMilsimInfantry" or "BitrunMilsimRifleman"));
                break;
        }

        foreach (var marker in run.Scenarios)
        {
            if (MetaData(marker).EntityName == "curiosity_spawn")
                run.Curiosities.Add(Spawn("BitrunEncryptedCuriosity", Transform(marker).Coordinates));
        }
    }

    private void PollObjectives(Run run)
    {
        if (run.Cache == null && run.Domain.Objective is BitrunObjective.Boss or BitrunObjective.Hunt or BitrunObjective.Island)
        {
            foreach (var target in run.Targets.ToArray())
            {
                if (!TerminatingOrDeleted(target) && !_mobState.IsDead(target))
                    continue;

                run.Targets.Remove(target);
                EntityCoordinates? location = TerminatingOrDeleted(target) ? null : Transform(target).Coordinates;
                AdvanceObjective(run, 1, run.Domain.Objective == BitrunObjective.Boss ? location : null);
            }

            if (run.Domain.Objective == BitrunObjective.Boss && run.Targets.Count == 0)
                SpawnCache(run);
            else if (run.Domain.Objective == BitrunObjective.Island && run.Targets.Count == 0 && run.Cache == null)
                PollIslandWave(run);
        }

        if (run.Domain.Objective == BitrunObjective.Stages && run.StageOpened &&
            run.Targets.All(mob => TerminatingOrDeleted(mob) || _mobState.IsDead(mob)))
            AdvanceObjective(run, 1);

        foreach (var curiosity in run.Curiosities.ToArray())
        {
            if (TerminatingOrDeleted(curiosity))
            {
                run.Curiosities.Remove(curiosity);
                continue;
            }

            if (!run.Goals.Any(goal => !TerminatingOrDeleted(goal) && IsNearby(goal, curiosity, GoalRange)) ||
                FindEquipment<BitrunForgeComponent>(run.Server, EquipmentRange) is not { } forge)
                continue;

            run.Curiosities.Remove(curiosity);
            QueueDel(curiosity);
            Spawn("BitrunDecryptedCuriosity", Transform(forge).Coordinates);
            NotifyAvatars(run, Loc.GetString("bitrun-curiosity-complete"));
        }
    }

    private void AdvanceObjective(Run run, int amount, EntityCoordinates? coordinates = null)
    {
        if (run.Completed || run.Cache != null)
            return;

        run.ObjectiveProgress = Math.Min(run.Domain.ObjectiveCount, run.ObjectiveProgress + amount);
        if (run.ObjectiveProgress >= run.Domain.ObjectiveCount)
        {
            SpawnCache(run, coordinates);
            NotifyAvatars(run, Loc.GetString("bitrun-cache-revealed"));
        }
        else
        {
            NotifyAvatars(run, Loc.GetString("bitrun-objective-progress",
                ("progress", run.ObjectiveProgress), ("required", run.Domain.ObjectiveCount)));
        }
    }

    private void PollIslandWave(Run run)
    {
        run.IslandWaveAt ??= _timing.CurTime + IslandWaveDelay;
        if (_timing.CurTime < run.IslandWaveAt)
            return;

        var attacker = run.Avatars.FirstOrDefault(uid => Comp<BitrunAvatarComponent>(uid).Side == BitrunSide.Attackers);
        if (!attacker.Valid || Transform(attacker).GridUid is not { } gridUid ||
            !TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        const float waveRange = 9f;
        var center = new Circle(_transform.GetWorldPosition(attacker), waveRange);
        var attackerCoordinates = Transform(attacker).Coordinates;
        var candidates = new List<EntityCoordinates>();
        var nearby = new List<EntityCoordinates>();
        foreach (var tile in _maps.GetTilesIntersecting(gridUid, grid, center))
        {
            if (_turf.IsSpace(tile) || !IsSpawnClear(tile, grid, avoidWater: true))
                continue;

            var coords = _maps.GridTileToLocal(gridUid, grid, tile.GridIndices);
            if (!coords.TryDistance(EntityManager, attackerCoordinates, out var distance) || distance < 2f)
                continue;

            nearby.Add(coords);
            if (_interaction.InRangeUnobstructed(attacker, coords, waveRange))
                candidates.Add(coords);
        }

        if (candidates.Count == 0)
            candidates = nearby.Count > 0 ? nearby : run.AttackerSpawns
                .Where(coords => coords.TryDistance(EntityManager, attackerCoordinates, out var distance) && distance >= 1f)
                .ToList();

        var count = Math.Min(4, run.Domain.ObjectiveCount - run.ObjectiveProgress);
        for (var index = 0; index < count && candidates.Count > 0; index++)
        {
            var coords = _random.PickAndTake(candidates);
            var crab = Spawn("BitrunIslandCrab", coords);
            EnsureComp<BitrunVirtualEntityComponent>(crab).Domain = run.Map;
            run.Targets.Add(crab);
        }

        if (run.Targets.Count == 0)
            return;

        run.IslandWaveAt = null;
        NotifyAvatars(run, Loc.GetString("bitrun-island-wave", ("count", run.Targets.Count)));
    }

    private void OnDrinkConsumed(EntityUid uid, BitrunAvatarComponent link, DrinkConsumedEvent args)
    {
        if (link.Side == BitrunSide.Attackers && _runs.TryGetValue(link.Server, out var run) &&
            run.Domain.Objective == BitrunObjective.Drinks && Transform(uid).MapUid == run.Map &&
            args.PrimaryReagent?.Id == "PinaColada")
            AdvanceObjective(run, 1);
    }

    private void OnScenarioInteract(EntityUid uid, BitrunScenarioMarkerComponent component, InteractHandEvent args)
    {
        if (args.Handled || !TryGetAttackObjective(uid, args.User, out var run) ||
            run.Cache != null || run.Completed)
            return;

        var markerName = MetaData(uid).EntityName;
        if (run.Domain.Objective == BitrunObjective.Ritual && markerName == "loot_signal")
        {
            args.Handled = true;
            var corpse = FindMapEntities<MobStateComponent>(run.Map).FirstOrDefault(mob =>
                _mobState.IsDead(mob) && MetaData(mob).EntityPrototype?.ID == "BitrunRitualCrew" &&
                !run.UsedScenarios.Contains(mob) && IsNearby(uid, mob, 1.5f));
            if (corpse.Valid)
            {
                run.UsedScenarios.Add(corpse);
                QueueDel(corpse);
                AdvanceObjective(run, 2);
            }
            else
            {
                _popup.PopupEntity(Loc.GetString("bitrun-ritual-needs-corpse"), uid, args.User);
            }
            return;
        }

        if (run.Domain.Objective == BitrunObjective.Fishing && IsFishingSpot(uid))
        {
            args.Handled = true;
            _popup.PopupEntity(Loc.GetString("bitrun-fishing-rod"), uid, args.User);
            return;
        }

        if (run.Domain.Objective != BitrunObjective.Stages || markerName != "ancient_stage" || run.UsedScenarios.Contains(uid))
            return;

        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User,
            TimeSpan.FromSeconds(3), new BitrunScenarioDoAfterEvent(), uid, target: uid)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        });
    }

    private void OnScenarioUse(EntityUid uid, BitrunScenarioMarkerComponent component, InteractUsingEvent args)
    {
        if (args.Handled || !HasComp<BitrunFishingRodComponent>(args.Used) || !IsFishingSpot(uid) ||
            !TryGetAttackObjective(uid, args.User, out var run) || run.Domain.Objective != BitrunObjective.Fishing ||
            run.Cache != null || !_hands.IsHolding(args.User, args.Used))
            return;

        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User,
            TimeSpan.FromSeconds(6), new BitrunScenarioDoAfterEvent(), uid, target: uid, used: args.Used)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        });
        if (args.Handled)
            _popup.PopupEntity(Loc.GetString("bitrun-fishing-cast"), uid, args.User);
    }

    private void OnScenarioDoAfter(EntityUid uid, BitrunScenarioMarkerComponent component, ref BitrunScenarioDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || !TryGetAttackObjective(uid, args.User, out var run))
            return;

        args.Handled = true;
        if (run.Domain.Objective == BitrunObjective.Fishing)
        {
            if (run.Cache == null && args.Used is { } rod && _hands.IsHolding(args.User, rod))
            {
                var fish = Spawn("FoodMeatFish", Transform(args.User).Coordinates);
                _hands.TryPickupAnyHand(args.User, fish);
                _popup.PopupEntity(Loc.GetString("bitrun-fishing-caught"), uid, args.User);
                AdvanceObjective(run, 1);
            }
            return;
        }

        if (run.Domain.Objective != BitrunObjective.Stages)
            return;

        if (!run.StageOpened)
        {
            run.StageOpened = true;
            foreach (var fog in FindMapEntities<MetaDataComponent>(run.Map))
            {
                if (MetaData(fog).EntityPrototype?.ID == "BitrunMilsimFog")
                    QueueDel(fog);
            }
            NotifyAvatars(run, Loc.GetString("bitrun-stage-open"));
        }

        if (run.Targets.Any(mob => !TerminatingOrDeleted(mob) && !_mobState.IsDead(mob)))
        {
            _popup.PopupEntity(Loc.GetString("bitrun-stage-hostiles"), uid, args.User);
            return;
        }

        if (run.UsedScenarios.Add(uid))
            AdvanceObjective(run, 1);
    }

    private bool IsFishingSpot(EntityUid uid) => MetaData(uid).EntityPrototype?.ID == "BitrunFishingSpot";

    private bool TryGetAttackObjective(EntityUid marker, EntityUid user, out Run run) =>
        TryGetObjective(marker, user, out run) && !run.Completed &&
        Comp<BitrunAvatarComponent>(user).Side == BitrunSide.Attackers;

    private bool TryGetObjective(EntityUid marker, EntityUid user, out Run run)
    {
        run = null!;
        return TryComp<BitrunAvatarComponent>(user, out var avatar) &&
               _runs.TryGetValue(avatar.Server, out run!) &&
               Transform(marker).MapUid == run.Map && _interaction.InRangeUnobstructed(user, marker);
    }

    private void StartEject(EntityUid pod, EntityUid actor)
    {
        if (GetPodBody(pod) is not { } target)
            return;

        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, actor, TimeSpan.FromSeconds(15),
            new BitrunEjectDoAfterEvent(GetNetEntity(target)), pod, target: pod)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        });
    }

    private void OnEjectDoAfter(EntityUid uid, BitrunPodComponent component, ref BitrunEjectDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || !_interaction.InRangeUnobstructed(args.User, uid) ||
            !TryGetEntity(args.Occupant, out var occupant))
            return;

        if (GetPodBody(uid) != occupant)
            return;

        args.Handled = true;
        if (_pods.TryGetValue(uid, out var avatar))
            Disconnect(avatar, true);
        else
            LeavePod(uid, occupant.Value);
    }

    private void ConfigureAvatarVision(Run run, EntityUid avatar)
    {
        if (run.Domain.EchoVision)
            SpawnInContainerOrDrop("BitrunEchoVisor", avatar, "eyes");
    }

    private static string GradeRun(Run run, TimeSpan elapsed)
    {
        var timeScore = elapsed.TotalMinutes switch
        {
            <= 1 => 10,
            <= 2 => 5,
            <= 5 => 3,
            <= 10 => 2,
            _ => 1,
        };

        return (run.Domain.Credits + timeScore * (run.Domain.Difficulty + 1)) switch
        {
            <= 4 => "D",
            <= 7 => "C",
            <= 10 => "B",
            <= 13 => "A",
            _ => "S",
        };
    }

    private EntProtoId[] GetRewardLoot(Run run, string grade)
    {
        var loot = new List<EntProtoId>(run.Domain.Loot);
        if (run.Random)
            loot.Add("IngotSilver1");

        if (GetWinningUsers(run, BitrunSide.Attackers).Skip(1).Any())
            loot.Add("SheetSteel1");

        if (run.Domain.Difficulty > 0 &&
            run.Avatars.Any(avatar => TryComp<BitrunAvatarComponent>(avatar, out var link) && link.Side == BitrunSide.Attackers && !link.TookDamage))
            loot.Add("SheetGlass1");

        if (run.Domain.Difficulty >= 2 && grade is "A" or "S")
            loot.Add("BitrunResearchDesignDisk");

        return loot.ToArray();
    }

    private void FillReward(EntityUid output, EntProtoId[] products)
    {
        foreach (var product in products)
            SpawnInContainerOrDrop(product, output, "entity_storage");
    }
}
