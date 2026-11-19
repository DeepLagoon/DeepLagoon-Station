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

[RegisterComponent]
public sealed partial class BitrunHuntTargetComponent : Component;

[RegisterComponent]
public sealed partial class BitrunMilsimEnemyComponent : Component;

[RegisterComponent]
public sealed partial class BitrunRitualOfferingComponent : Component
{
    [DataField] public int Value = 1;
}

public sealed partial class BitrunSystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;

    private void InitializeObjectives()
    {
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, InteractHandEvent>(OnScenarioInteract);
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, InteractUsingEvent>(OnScenarioUse);
        SubscribeLocalEvent<BitrunScenarioMarkerComponent, BitrunScenarioDoAfterEvent>(OnScenarioDoAfter);
        SubscribeLocalEvent<BitrunPodComponent, BitrunEjectDoAfterEvent>(OnEjectDoAfter);
        SubscribeLocalEvent<BitrunAvatarComponent, DrinkConsumedEvent>(OnDrinkConsumed);
        SubscribeLocalEvent<BitrunRitualOfferingComponent, MoveEvent>(OnOfferingMoved);
        SubscribeLocalEvent<BitrunRitualOfferingComponent, MobStateChangedEvent>(OnOfferingDied);
    }

    private void OnOfferingMoved(Entity<BitrunRitualOfferingComponent> offering, ref MoveEvent args)
    {
        if (!args.OnlyRotation)
            TrySacrifice(offering);
    }

    private void OnOfferingDied(Entity<BitrunRitualOfferingComponent> offering, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
            TrySacrifice(offering);
    }

    private bool TrySacrifice(Entity<BitrunRitualOfferingComponent> offering)
    {
        var xform = Transform(offering);
        if (!_mobState.IsDead(offering) || xform.MapUid is not { } map || !_domains.TryGetValue(map, out var run) ||
            run.Completed || run.Cache != null || run.Domain.Objective != BitrunObjective.Ritual ||
            run.UsedScenarios.Contains(offering) || xform.GridUid is not { } grid ||
            !TryComp<MapGridComponent>(grid, out var gridComp))
            return false;
        var position = _maps.CoordinatesToTile(grid, gridComp, xform.Coordinates);
        if (!run.Scenarios.Any(marker => !TerminatingOrDeleted(marker) && MetaData(marker).EntityName == "loot_signal" &&
            Transform(marker).GridUid == grid && _maps.CoordinatesToTile(grid, gridComp, Transform(marker).Coordinates) == position))
            return false;
        run.UsedScenarios.Add(offering);
        AdvanceObjective(run, offering.Comp.Value, xform.Coordinates);
        QueueDel(offering);
        return true;
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
                    // Imported appearance variants use BitrunMapMob... IDs.
                    // Identify the actual boss capability, not its prototype spelling.
                    if (mobs.FirstOrDefault(uid => HasComp<BitrunMegafaunaComponent>(uid) && IsNearby(marker, uid, 1.5f)) is { Valid: true } boss)
                        run.Targets.Add(boss);
                }
                break;

            case BitrunObjective.Hunt:
                run.Targets.UnionWith(mobs.Where(uid => HasComp<BitrunHuntTargetComponent>(uid) || MetaData(uid).EntityPrototype?.ID == "BitrunHuntAnimal"));
                break;

            case BitrunObjective.Stages:
                run.Targets.UnionWith(mobs.Where(uid => HasComp<BitrunMilsimEnemyComponent>(uid) || MetaData(uid).EntityPrototype?.ID is
                    "BitrunMilsimInfantry" or "BitrunMilsimRifleman"));
                break;
        }

        foreach (var marker in run.Scenarios)
        {
            if (MetaData(marker).EntityName == "curiosity_spawn")
                run.Curiosities.Add(Spawn("BitrunEncryptedCuriosity", Transform(marker).Coordinates));
        }
    }

    internal void RecordMegafaunaDeath(EntityUid boss)
    {
        if (Transform(boss).MapUid is not { } map || !_domains.TryGetValue(map, out var run) ||
            run.Domain.Objective != BitrunObjective.Boss || !run.Targets.Remove(boss))
            return;
        AdvanceObjective(run, 1, Transform(boss).Coordinates);
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
            var accepted = false;
            foreach (var corpse in FindMapEntities<BitrunRitualOfferingComponent>(run.Map))
                accepted |= TrySacrifice((corpse, Comp<BitrunRitualOfferingComponent>(corpse)));
            if (!accepted)
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
        return ScoreRun(run, elapsed) switch
        {
            <= 4 => "D",
            <= 7 => "C",
            <= 10 => "B",
            <= 13 => "A",
            _ => "S",
        };
    }

    private static int ScoreRun(Run run, TimeSpan elapsed)
    {
        var timeScore = elapsed.TotalMinutes switch
        {
            <= 1 => 10,
            <= 2 => 5,
            <= 5 => 3,
            <= 10 => 2,
            _ => 1,
        };

        return run.Threats.Count * 5 + run.Domain.Credits + timeScore * (run.Domain.Difficulty + 1);
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
