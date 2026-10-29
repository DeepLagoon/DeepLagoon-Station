using System.Linq;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Client._DeepLagoon.WebUI;
using Robust.Shared.Localization;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private HashSet<string> _combineMarkings = new();
    private bool CanUseMarkingGlow => _entManager.System<Content.Shared._DeepLagoon.DiscordLink.SharedDiscordBoostSystem>()
        .CanUseMarkingGlow(_playerManager.LocalSession);

    private static string LimbCategory(HumanoidVisualLayers part) => part switch
    {
        HumanoidVisualLayers.LArm or HumanoidVisualLayers.RArm => "Arms",
        HumanoidVisualLayers.LHand or HumanoidVisualLayers.RHand => "Hands",
        HumanoidVisualLayers.LLeg or HumanoidVisualLayers.RLeg => "Legs",
        HumanoidVisualLayers.LFoot or HumanoidVisualLayers.RFoot => "Feet",
        _ => "",
    };

    private string MarkingCategoryKey(MarkingPrototype prototype) => LimbCategory(prototype.BodyPart) is { Length: > 0 } limb ? limb
        : _markingManager.IsCommon(prototype) ? "common" : prototype.MarkingCategory.ToString();

    private string MarkingCategoryName(MarkingPrototype prototype) => Loc.GetString("markings-category-" + MarkingCategoryKey(prototype));

    private MarkingPrototype CanonicalMarking(MarkingPrototype prototype) => MarkingManager.IsRightPart(prototype.BodyPart)
        ? _markingManager.EditorMirror(prototype) ?? prototype : prototype;

    private static string PairName(string name)
    {
        foreach (var side in new[] { "левая ", "правая ", "левый ", "правый ", "левое ", "правое ", "левой ", "правой ", "left ", "right " })
            name = name.Replace(side, "", StringComparison.OrdinalIgnoreCase);
        return name;
    }

    private IEnumerable<MarkingPrototype> PairParts(MarkingPrototype prototype)
    {
        yield return prototype;
        if (_markingManager.EditorMirror(prototype) is { } mirror) yield return mirror;
    }

    private IEnumerable<Marking> SelectedPair(List<Marking> list, MarkingPrototype prototype) =>
        list.Where(marking => PairParts(prototype).Any(part => part.ID == marking.MarkingId));

    private IEnumerable<TguiData> MarkingSelectionData()
    {
        var list = Profile!.Appearance.Markings;
        foreach (var (marking, index) in list.Select((marking, index) => (marking, index)).OrderByDescending(entry => entry.marking.DrawOrder))
        {
            if (!_markingManager.TryGetMarking(marking, out var definition) || definition.EditorHidden) continue;
            var canonical = CanonicalMarking(definition);
            if (canonical.ID != definition.ID && list.Any(other => other.MarkingId == canonical.ID)) continue;
            var paired = _markingManager.EditorMirror(definition) != null;
            var members = SelectedPair(list, definition).ToArray();
            yield return new TguiData().Number("index", index).String("id", canonical.ID)
                .String("name", paired ? PairName(Loc.GetString($"marking-{canonical.ID}")) : Loc.GetString($"marking-{canonical.ID}"))
                .String("part", definition.BodyPart.ToString()).String("categoryId", MarkingCategoryKey(definition))
                .String("category", MarkingCategoryName(definition)).Bool("paired", paired)
                .String("side", members.Length > 1 ? "both" : MarkingManager.IsRightPart(definition.BodyPart) ? "right" : "left")
                .Bool("canGlow", definition.CanGlow).Bool("glow", marking.Unshaded && CanUseMarkingGlow)
                .Bool("glowLocked", !CanUseMarkingGlow)
                .Bool("locked", definition.ForcedColoring || _markingManager.MustMatchSkin(Profile.Species, definition.BodyPart, out _, _prototypeManager))
                .Array("colors", marking.MarkingColors.Select(color => Choice("", color.ToHexNoAlpha())));
        }
    }

    private IEnumerable<TguiData> MarkingOptionData() => _markingManager.Markings.Values.Where(m =>
            m.MarkingCategory is not (MarkingCategories.Hair or MarkingCategories.FacialHair) && !m.EditorHidden
            && CanonicalMarking(m).ID == m.ID
            && _markingManager.CanBeApplied(Profile!.Species, Profile.Sex, m, _prototypeManager, Profile.UnrestrictedMarkings))
        .Select(m => Choice(m.ID, _markingManager.EditorMirror(m) != null ? PairName(Loc.GetString($"marking-{m.ID}")) : Loc.GetString($"marking-{m.ID}"))
            .String("category", MarkingCategoryName(m)).String("combinationCategory", MarkingCategoryKey(m))
            .String("part", m.BodyPart.ToString()).String("categoryId", MarkingCategoryKey(m))
            .String("typeId", m.FilterType?.ToString() ?? "")
            .Array("races", MarkingRaceChoices(m)).String("iconId", "marking/" + m.ID));

    private void DisableMarkingCombination(string category)
    {
        if (Profile == null) return;
        var occupied = new HashSet<HumanoidVisualLayers>();
        var selected = Profile.Appearance.Markings.OrderByDescending(marking => marking.DrawOrder)
            .Where(marking => !_markingManager.TryGetMarking(marking, out var prototype) || MarkingCategoryKey(prototype) != category
                || occupied.Add(prototype.BodyPart)).ToList();
        var set = new MarkingSet(selected, _prototypeManager.Index(Profile.Species).MarkingPoints,
            _markingManager, _prototypeManager, Profile.UnrestrictedMarkings);
        OnMarkingChange(set);
    }

    private bool TrySelectTguiMarking(List<Marking> list, MarkingPrototype selected, string points, string side = "both", Marking? template = null)
    {
        if (Profile == null)
            return false;

        var bundle = PairParts(selected).Where(part => side == "both"
            || (side == "right") == MarkingManager.IsRightPart(part.BodyPart)).ToList();
        if (bundle.Count == 0 || bundle.Any(part => !_markingManager.CanBeApplied(Profile.Species, Profile.Sex, part,
                _prototypeManager, Profile.UnrestrictedMarkings))) return false;
        foreach (var id in selected.EditorLinkedMarkings)
        {
            if (!_markingManager.Markings.TryGetValue(id, out var linked)
                || !_markingManager.CanBeApplied(Profile.Species, Profile.Sex, linked, _prototypeManager, Profile.UnrestrictedMarkings))
                return false;
            bundle.Add(linked);
        }

        RestoreTguiBodyMarkings(list, points);

        var defaults = _prototypeManager.Index<MarkingPointsPrototype>(points).Points.Values
            .SelectMany(category => category.DefaultMarkings).ToHashSet();
        list.RemoveAll(marking => defaults.Contains(marking.MarkingId) && _markingManager.TryGetMarking(marking, out var existing)
            && bundle.Any(part => part.IsBodyReplacement && part.BodyPart == existing.BodyPart));

        if (!_combineMarkings.Contains(MarkingCategoryKey(selected)))
            list.RemoveAll(marking => _markingManager.TryGetMarking(marking, out var existing)
                && bundle.Any(part => part.BodyPart == existing.BodyPart &&
                    MarkingCategoryKey(part) == MarkingCategoryKey(existing)));
        else if (bundle.All(part => list.Any(marking => marking.MarkingId == part.ID)))
            return false;

        var order = NormalizeTguiMarkingOrder(list) + 1;

        var set = new MarkingSet(list, points, _markingManager, _prototypeManager, Profile.UnrestrictedMarkings);
        foreach (var part in bundle)
        {
            if (list.Any(marking => marking.MarkingId == part.ID))
                continue;
            if (set.PointsLeft(part.MarkingCategory) == 0)
                return false;
            var marking = part.AsMarking();
            marking.DrawOrder = template?.DrawOrder ?? order;
            marking.Unshaded = template?.Unshaded == true && part.CanGlow;
            var colors = MarkingColoring.GetMarkingLayerColors(part, Profile.Appearance.SkinColor, Profile.Appearance.EyeColor, set);
            for (var i = 0; i < colors.Count; i++)
                marking.SetColor(i, colors[i]);
            if (_markingManager.MustMatchSkin(Profile.Species, part.BodyPart, out var alpha, _prototypeManager))
                marking.SetColor(Profile.Appearance.SkinColor.WithAlpha(alpha));
            else if (template != null)
                for (var i = 0; i < Math.Min(marking.MarkingColors.Count, template.MarkingColors.Count); i++)
                    marking.SetColor(i, template.MarkingColors[i]);
            set.AddBack(part.MarkingCategory, marking);
            list.Add(marking);
        }
        return true;
    }

    private int NormalizeTguiMarkingOrder(List<Marking> list)
    {
        var ordered = list.GroupBy(marking => _markingManager.TryGetMarking(marking, out var prototype)
                ? CanonicalMarking(prototype).ID : marking.MarkingId)
            .OrderByDescending(group => group.Max(marking => marking.DrawOrder)).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            foreach (var marking in ordered[i]) marking.DrawOrder = ordered.Length - i;
        return ordered.Length;
    }

    private bool MoveTguiMarking(List<Marking> list, Marking selected, int direction)
    {
        if (!_markingManager.TryGetMarking(selected, out var prototype))
            return false;
        var species = _prototypeManager.Index(Profile!.Species);
        var anchor = prototype.GetLayerAnchor(species);
        static int Side(HumanoidVisualLayers part) => part switch
        {
            HumanoidVisualLayers.LArm or HumanoidVisualLayers.LHand or HumanoidVisualLayers.LLeg or HumanoidVisualLayers.LFoot => -1,
            HumanoidVisualLayers.RArm or HumanoidVisualLayers.RHand or HumanoidVisualLayers.RLeg or HumanoidVisualLayers.RFoot => 1,
            _ => 0,
        };
        var ordered = list.Where(marking => _markingManager.TryGetMarking(marking, out var part)
                && !part.EditorHidden && part.GetLayerAnchor(species) == anchor && Side(part.BodyPart) == Side(prototype.BodyPart))
            .OrderByDescending(marking => marking.DrawOrder).ToList();
        var otherIndex = ordered.IndexOf(selected) + direction;
        if (otherIndex < 0 || otherIndex >= ordered.Count)
            return false;
        NormalizeTguiMarkingOrder(list);
        var other = ordered[otherIndex];
        (selected.DrawOrder, other.DrawOrder) = (other.DrawOrder, selected.DrawOrder);
        foreach (var parent in new[] { selected, other })
        {
            var definition = _markingManager.Markings[parent.MarkingId];
            foreach (var linked in SelectedPair(list, definition))
                linked.DrawOrder = parent.DrawOrder;
        }
        return true;
    }

    private void RestoreTguiBodyMarkings(List<Marking> list, string points)
    {
        if (Profile == null)
            return;
        var defaults = _prototypeManager.Index<MarkingPointsPrototype>(points);
        foreach (var id in defaults.Points.Values.SelectMany(category => category.DefaultMarkings))
        {
            if (!_markingManager.Markings.TryGetValue(id, out var part) || !part.EditorExclusive
                || list.Any(marking => _markingManager.TryGetMarking(marking, out var existing)
                    && existing.MarkingCategory == part.MarkingCategory && existing.BodyPart == part.BodyPart))
                continue;
            var marking = part.AsMarking();
            marking.SetColor(Profile.Appearance.SkinColor);
            list.Add(marking);
        }
    }
}
