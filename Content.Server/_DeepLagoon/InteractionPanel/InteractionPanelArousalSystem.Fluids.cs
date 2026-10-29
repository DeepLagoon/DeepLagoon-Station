using System.Linq;
using Content.Server.Fluids.EntitySystems;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid;
using Content.Shared.Mood;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Forensics.Components;
using Content.Shared.Body.Systems;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Robust.Shared.Random;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelArousalSystem
{
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private PuddleSystem _puddles = default!;
    private const string CoatingSolution = "bluemoon-coating";
    private const float InternalFluidCapacity = 300;
    private static readonly TimeSpan PregnancyStageDuration = TimeSpan.FromMinutes(15);

    private void InitializePhysiology()
    {
        SubscribeLocalEvent<BlueMoonProtectionComponent, UseInHandEvent>(OnUnwrapProtection);
        SubscribeLocalEvent<BlueMoonProtectionComponent, SolutionContainerChangedEvent>((uid, component, _) => UpdateProtectionVisual(uid, component));
        SubscribeLocalEvent<BlueMoonPhysiologyComponent, ExaminedEvent>(OnPhysiologyExamine);
        SubscribeLocalEvent<BlueMoonPhysiologyComponent, InteractUsingEvent>(OnCleanPhysiology);
    }

    public string[] ProducingParts(EntityUid uid) => HasComp<Content.Shared.Silicons.Borgs.Components.BorgChassisComponent>(uid) ? ["synthetic"] : TryComp<HumanoidAppearanceComponent>(uid, out var body)
        ? body.BodyFeatures.Where(f => f.Enabled && (f.Part == "breasts"
            || f.Part == "penis" && HasFeature(uid, "testicles")
            || f.Part == "vagina" && HasFeature(uid, "womb"))).Select(f => f.Part).Distinct().ToArray() : [];

    private bool HasFeature(EntityUid uid, string part) =>
        TryComp<HumanoidAppearanceComponent>(uid, out var appearance) && appearance.BodyFeatures.Any(f => f.Part == part && f.Enabled);

    public bool CanExposePart(EntityUid uid, string part)
    {
        if (IsChastened(uid, part)) return false;
        if (part == "synthetic") return ProducingParts(uid).Contains(part);
        if (part != "mouth" && !HasFeature(uid, part)) return false;
        if (TryComp<HumanoidAppearanceComponent>(uid, out var body)
            && body.BodyFeatures.Any(f => f.Part == part && f.Enabled && f.Visibility == 2)) return true;
        var inventory = EntityManager.System<InventorySystem>();
        if (part == "mouth") return !inventory.TryGetSlotEntity(uid, "mask", out _);
        return !inventory.TryGetSlotEntity(uid, "jumpsuit", out _) && !inventory.TryGetSlotEntity(uid, "outerClothing", out _);
    }

    public float AvailableFluid(EntityUid uid, string part)
    {
        if (!ProducingParts(uid).Contains(part)) return 0;
        var state = EnsureComp<BlueMoonPhysiologyComponent>(uid);
        var settings = Preferences(uid);
        if (part == "synthetic") return settings.SyntheticVolume;
        var rate = part switch { "breasts" => settings.MilkRate, "vagina" => settings.FemaleRate, _ => settings.FluidRate };
        var multiplier = part switch { "breasts" => settings.MilkMultiplier, "vagina" => settings.FemaleMultiplier, _ => settings.FluidMultiplier };
        var maximum = part switch { "breasts" => settings.MilkMaximum, "vagina" => settings.FemaleMaximum, _ => settings.FluidMaximum };
        if (part == "breasts" && HasFeature(uid, "womb")) rate *= 2;
        return BlueMoonArousalRules.FluidAmount((float)(_timing.CurTime - state.LastEmission.GetValueOrDefault(part)).TotalSeconds,
            rate, multiplier, maximum);
    }

    private void EmitClimaxFluids(EntityUid source, EntityUid? partner, string? selectedPart, string? destination, EntityUid? container)
    {
        var state = EnsureComp<BlueMoonPhysiologyComponent>(source);
        var preferences = Preferences(source);
        var emitted = false;
        foreach (var part in ProducingParts(source))
        {
            if (selectedPart != null && part != selectedPart || IsChastened(source, part)) continue;
            var sensitivity = preferences.Sensitivity(part);
            if (sensitivity is BlueMoonSensitivity.Impotent or BlueMoonSensitivity.Disappointing) continue;
            var fluid = new Solution();
            fluid.AddReagent(new ReagentId(part switch { "breasts" => "DLPhysiologyMilk", "vagina" => "DLFemaleEjaculate", "synthetic" when preferences.GenitalFluid == "DLSemen" => "DLSynthLubricant", _ => preferences.GenitalFluid == "Milk" ? "DLPhysiologyMilk" : preferences.GenitalFluid },
                    TryComp<DnaComponent>(source, out var dna) ? [new DnaData { DNA = dna.DNA }] : null),
                FixedPoint2.New(AvailableFluid(source, part)));
            state.LastEmission[part] = _timing.CurTime;
            emitted = true;
            if (fluid.Volume <= 0) continue;
            var spillAt = source;
            if (CaptureProtectionFluid(source, part, fluid)) continue;
            if (container is { } vessel && _solutions.TryGetRefillableSolution(vessel, out var vesselSolution, out var vesselContents))
                _solutions.TryAddSolution(vesselSolution.Value, fluid.SplitSolution(FixedPoint2.Min(fluid.Volume, vesselContents.AvailableVolume)));
            else if (partner is { } recipient && Preferences(recipient).ReceiveFluids && EroticEffectsAllowed(recipient)
                && destination != null && destination != "surface")
            {
                spillAt = recipient;
                ReceiveFluid(source, recipient, part, destination, fluid);
            }
            else if (!preferences.Messy && partner is { } surface && Preferences(surface).ReceiveFluids && EroticEffectsAllowed(surface))
            {
                spillAt = surface;
                AddFluidToBody(surface, CoatingSolution, fluid, InternalFluidCapacity);
            }
            Spill(spillAt, fluid);
        }
        if (emitted)
        {
            state.LastClimax = _timing.CurTime;
            RaiseLocalEvent(source, new MoodEffectEvent("DLOrgasm"));
            RemoveAphrodisiacs(source);
        }
    }

    private void ReceiveFluid(EntityUid source, EntityUid target, string sourcePart, string destination, Solution fluid)
    {
        if (destination is "breasts" or "penis")
        {
            if (CanExposePart(target, destination)) AddFluidToBody(target, CoatingSolution, fluid, InternalFluidCapacity);
            return;
        }
        destination = destination switch { "nipples" => "breasts", "urethra" => "penis", _ => destination };
        if (destination is not ("mouth" or "vagina" or "anus" or "breasts" or "belly" or "penis")) return;
        if (!CanExposePart(target, destination) || sourcePart == "vagina" && destination != "mouth") return;
        var volume = (float)fluid.Volume;
        var hasSemen = fluid.Contents.Any(r => r.Reagent.Prototype == "DLSemen");
        AddFluidToBody(target, "bluemoon-internal-" + destination, fluid, InternalFluidCapacity);
        ApplyFluidGrowth(target, destination, volume - (float)fluid.Volume);
        if (sourcePart == "penis" && destination == "vagina" && hasSemen && volume > (float)fluid.Volume)
            TryStartPregnancy(source, target);
    }

    private void AddFluidToBody(EntityUid target, string name, Solution fluid, float capacity)
    {
        _solutions.EnsureSolution(target, name, out _, FixedPoint2.New(capacity));
        if (!_solutions.TryGetSolution(target, name, out var solution, out var contents)) return;
        var accepted = fluid.SplitSolution(FixedPoint2.Min(fluid.Volume, contents.AvailableVolume));
        _solutions.TryAddSolution(solution.Value, accepted);
        EnsureComp<BlueMoonPhysiologyComponent>(target);
    }

    private void Spill(EntityUid uid, Solution fluid)
    {
        if (fluid.Volume > 0) _puddles.TrySpillAt(uid, fluid, out _, sound: false);
    }

    private void ApplyFluidGrowth(EntityUid uid, string destination, float amount)
    {
        var preferences = Preferences(uid);
        if (amount <= 0 || !preferences.FluidGrowth || !_bodyCustomization.AllowsPhysiologyChanges(uid)) return;
        var part = destination switch { "vagina" or "mouth" => "belly", "anus" => "butt", _ => destination };
        if (!BodyCustomizationCatalog.CanResize(part) || !TryComp<HumanoidAppearanceComponent>(uid, out var body)) return;
        var state = EnsureComp<BlueMoonPhysiologyComponent>(uid);
        if (state.Pregnant && part == "belly") return;
        var volume = state.GrowthVolumes.GetValueOrDefault(part) + amount;
        var capacity = part switch { "breasts" => preferences.MilkMaximum, "vagina" => preferences.FemaleMaximum, _ => preferences.FluidMaximum };
        var threshold = Math.Max(1, capacity * .5f);
        var steps = (int)Math.Min(300, volume / threshold);
        state.GrowthVolumes[part] = volume % threshold;
        if (steps == 0) return;
        var features = BodyCustomizationCatalog.Sanitize(body.BodyFeatures);
        var feature = features.FirstOrDefault(f => f.Part == part && f.Enabled);
        if (feature == null)
        {
            if (part is not ("belly" or "butt")) return;
            feature = BodyCustomizationCatalog.Default(part);
            feature.Enabled = true;
            features.RemoveAll(f => f.Part == part);
            features.Add(feature);
        }
        for (var i = 0; i < steps; i++)
            if (!BodyCustomizationCatalog.Resize(features, part, 1)) break;
        _bodyCustomization.SetPhysiologyFeature(uid, part, features.First(f => f.Part == part));
    }

    public bool TryStartPregnancy(EntityUid source, EntityUid target)
    {
        var receiver = Preferences(target);
        if (!receiver.Pregnancy || !receiver.ReceiveFluids || !EroticEffectsAllowed(target) || !HasFeature(target, "womb")
            || !HasFeature(target, "vagina") || !_bodyCustomization.AllowsPhysiologyChanges(target)) return false;
        var state = EnsureComp<BlueMoonPhysiologyComponent>(target);
        if (state.Pregnant || receiver.Fertility <= 0 || Preferences(source).Virility <= 0) return false;
        var chance = BlueMoonArousalRules.FertilizationChance(Preferences(source).Virility, receiver.Fertility, _random.NextFloat(-.15f, .15f));
        if (receiver.EstrousCycle || TryComp<InteractionPanelArousalComponent>(target, out var arousal) && arousal.InHeat)
        {
            state.HeatStarted ??= _timing.CurTime;
            var since = state.LastClimax > state.HeatStarted.Value ? state.LastClimax : state.HeatStarted.Value;
            chance += BlueMoonArousalRules.HeatFertilityBonus((float)(_timing.CurTime - since).TotalSeconds);
        }
        if (!_random.Prob(Math.Clamp(chance, 0, 100) / 100)) return false;
        state.Pregnant = true;
        state.PregnancyStarted = _timing.CurTime;
        state.OriginalBelly = Comp<HumanoidAppearanceComponent>(target).BodyFeatures.FirstOrDefault(f => f.Part == "belly")?.Clone();
        UpdatePregnancy(target, state);
        return true;
    }

    public void EndPregnancy(EntityUid uid)
    {
        if (!TryComp<BlueMoonPhysiologyComponent>(uid, out var state) || !state.Pregnant) return;
        state.Pregnant = false;
        if (TryComp<HumanoidAppearanceComponent>(uid, out var body)
            && body.BodyFeatures.FirstOrDefault(f => f.Part == "belly") is { } current
            && state.PregnancyBelly != null && current.ContentEquals(state.PregnancyBelly))
            _bodyCustomization.SetPhysiologyFeature(uid, "belly", state.OriginalBelly);
        state.OriginalBelly = state.PregnancyBelly = null;
        state.PregnancyStage = 0;
    }

    private void UpdatePregnancy(EntityUid uid, BlueMoonPhysiologyComponent state)
    {
        if (!state.Pregnant) return;
        if (!Preferences(uid).Pregnancy || !Preferences(uid).ReceiveFluids || !EroticEffectsAllowed(uid)
            || !HasFeature(uid, "womb") || !_bodyCustomization.AllowsPhysiologyChanges(uid))
        {
            EndPregnancy(uid);
            return;
        }
        var stage = Math.Clamp((int)((_timing.CurTime - state.PregnancyStarted).TotalSeconds / PregnancyStageDuration.TotalSeconds) + 1, 1, 4);
        if (stage == state.PregnancyStage) return;
        var belly = state.OriginalBelly?.Clone() ?? BodyCustomizationCatalog.Default("belly");
        belly.Enabled = true;
        var sizes = BodyCustomizationCatalog.Sizes("belly", belly.Shape);
        var initial = Math.Max(0, Array.IndexOf(sizes, belly.Size));
        belly.Size = sizes[Math.Min(sizes.Length - 1, initial + stage)];
        if (_bodyCustomization.SetPhysiologyFeature(uid, "belly", belly))
        {
            state.PregnancyStage = stage;
            state.PregnancyBelly = belly.Clone();
        }
    }

    private void UpdatePhysiology(EntityUid uid)
    {
        if (!TryComp<BlueMoonPhysiologyComponent>(uid, out var state)) return;
        if (Preferences(uid).EstrousCycle || TryComp<InteractionPanelArousalComponent>(uid, out var arousal) && arousal.InHeat)
            state.HeatStarted ??= _timing.CurTime;
        UpdatePregnancy(uid, state);
        UpdateDrugDependence(uid, state);
        if (_timing.CurTime < state.NextDrip) return;
        state.NextDrip = _timing.CurTime + TimeSpan.FromSeconds(5);
        foreach (var name in new[] { CoatingSolution, "bluemoon-internal-vagina", "bluemoon-internal-anus", "bluemoon-internal-breasts", "bluemoon-internal-belly", "bluemoon-internal-penis" })
            if (_solutions.TryGetSolution(uid, name, out var solution, out var contents) && contents.Volume > 0)
                Spill(uid, _solutions.SplitSolution(solution.Value, FixedPoint2.New(1)));
        if (_solutions.TryGetSolution(uid, "bluemoon-internal-mouth", out var mouth, out var mouthContents) && mouthContents.Volume > 0)
        {
            var swallowed = _solutions.SplitSolution(mouth.Value, FixedPoint2.New(1));
            foreach (var organ in EntityManager.System<SharedBodySystem>().GetBodyOrganEntityComps<StomachComponent>(uid))
                if (EntityManager.System<StomachSystem>().TryTransferSolution(organ.Owner, swallowed))
                {
                    swallowed.RemoveAllSolution();
                    break;
                }
            Spill(uid, swallowed);
        }
    }

    private void RemoveAphrodisiacs(EntityUid uid)
    {
        if (!_solutions.TryGetSolution(uid, "chemicals", out var solution, out var contents)) return;
        foreach (var (id, minimum, maximum, divisor) in new[] { ("DLCrocin", 10, 30, 5), ("DLHexacrocin", 2, 15, 6) })
        {
            var quantity = contents.GetTotalPrototypeQuantity(id);
            if (quantity > 0) _solutions.RemoveReagent(solution.Value, id,
                FixedPoint2.New(_random.NextFloat(minimum, Math.Max(maximum, (float)quantity / divisor))));
        }
    }

    private void OnPhysiologyExamine(EntityUid uid, BlueMoonPhysiologyComponent state, ExaminedEvent args)
    {
        if (state.Pregnant) args.PushText(Loc.GetString("dl-arousal-pregnancy-examine", ("name", Name(uid))));
        if (_solutions.TryGetSolution(uid, CoatingSolution, out _, out var coating) && coating.Volume > 0)
            args.PushText(Loc.GetString("dl-arousal-coated-examine"));
    }

    private void OnCleanPhysiology(EntityUid uid, BlueMoonPhysiologyComponent state, InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<LustWishWipeComponent>(args.Used, out var wipe) || wipe.Used
            || !_solutions.TryGetSolution(uid, CoatingSolution, out var coating, out var contents) || contents.Volume <= 0
            || !_solutions.TryGetSolution(args.Used, "absorbed", out var absorbent, out var absorbed) || absorbed.AvailableVolume <= 0) return;
        var removed = _solutions.SplitSolution(coating.Value, FixedPoint2.Min(contents.Volume, absorbed.AvailableVolume));
        _solutions.TryAddSolution(absorbent.Value, removed);
        args.Handled = true;
    }
}
