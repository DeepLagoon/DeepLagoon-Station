using System.Linq;
using Content.Server.Nutrition.EntitySystems;
using Content.Server.Ghost.Roles.Events;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunIslandCombatantComponent : Component;

public sealed partial class BitrunSystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;

    private void InitializeObjectives()
    {
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, InteractHandEvent>(OnScenarioInteract);
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, BitrunScenarioDoAfterEvent>(OnScenarioDoAfter);
        SubscribeLocalEvent<BitrunPodComponent, BitrunEjectDoAfterEvent>(OnEjectDoAfter);
        SubscribeLocalEvent<BitrunAvatarComponent, DrinkConsumedEvent>(OnDrinkConsumed);
        SubscribeLocalEvent<BitrunIslandCombatantComponent, GhostRoleSpawnerUsedEvent>(OnIslandCombatantSpawned);
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

    private void OnIslandCombatantSpawned(EntityUid uid, BitrunIslandCombatantComponent component, GhostRoleSpawnerUsedEvent args)
    {
        if (Transform(uid).MapUid is { } map && _domains.TryGetValue(map, out var run) &&
            run.Domain.Objective == BitrunObjective.Island)
            run.Targets.Add(uid);
    }

    private void OnDrinkConsumed(EntityUid uid, BitrunAvatarComponent link, DrinkConsumedEvent args)
    {
        if (link.Side == BitrunSide.Attackers && _runs.TryGetValue(link.Server, out var run) &&
            run.Domain.Objective == BitrunObjective.Drinks &&
            MetaData(args.Drink).EntityPrototype?.ID == "DrinkPinaColadaGlass")
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

        var fishing = run.Domain.Objective == BitrunObjective.Fishing && markerName == "fishing";
        var stage = run.Domain.Objective == BitrunObjective.Stages && markerName == "ancient_stage";
        if (!fishing && !stage || run.UsedScenarios.Contains(uid))
            return;

        if (fishing && !HasFishingRod(args.User))
        {
            args.Handled = true;
            _popup.PopupEntity(Loc.GetString("bitrun-fishing-rod"), uid, args.User);
            return;
        }

        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User,
            TimeSpan.FromSeconds(fishing ? 6 : 3), new BitrunScenarioDoAfterEvent(), uid, target: uid)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        });
    }

    private void OnScenarioDoAfter(EntityUid uid, BitrunScenarioMarkerComponent component, ref BitrunScenarioDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || !TryGetAttackObjective(uid, args.User, out var run))
            return;

        args.Handled = true;
        if (run.Domain.Objective == BitrunObjective.Fishing)
        {
            if (HasFishingRod(args.User))
            {
                var fish = Spawn("FoodMeatFish", Transform(args.User).Coordinates);
                _hands.TryPickupAnyHand(args.User, fish);
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

    private bool HasFishingRod(EntityUid user) =>
        _hands.EnumerateHeld(user).Any(item => MetaData(item).EntityPrototype?.ID == "BitrunFishingRod");

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
