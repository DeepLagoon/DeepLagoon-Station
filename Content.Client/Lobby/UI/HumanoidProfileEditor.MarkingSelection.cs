using System.Linq;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private bool _combineMarkings;

    private void DisableMarkingCombination()
    {
        if (Profile == null) return;
        var occupied = new HashSet<(HumanoidVisualLayers, MarkingCategories)>();
        var selected = Profile.Appearance.Markings.OrderByDescending(marking => marking.DrawOrder)
            .Where(marking => !_markingManager.TryGetMarking(marking, out var prototype)
                || occupied.Add((prototype.BodyPart, prototype.BodyPart is HumanoidVisualLayers.Tail or HumanoidVisualLayers.Snout
                    ? MarkingCategories.Special : prototype.MarkingCategory))).ToList();
        var set = new MarkingSet(selected, _prototypeManager.Index(Profile.Species).MarkingPoints,
            _markingManager, _prototypeManager, Profile.UnrestrictedMarkings);
        OnMarkingChange(set);
    }

    private bool TrySelectTguiMarking(List<Marking> list, MarkingPrototype selected, string points)
    {
        if (Profile == null)
            return false;

        var bundle = new List<MarkingPrototype> { selected };
        foreach (var id in selected.EditorLinkedMarkings)
        {
            if (!_markingManager.Markings.TryGetValue(id, out var linked)
                || !_markingManager.CanBeApplied(Profile.Species, Profile.Sex, linked, _prototypeManager, Profile.UnrestrictedMarkings))
                return false;
            bundle.Add(linked);
        }

        RestoreTguiBodyMarkings(list, points);

        if (!_combineMarkings)
            list.RemoveAll(marking => _markingManager.TryGetMarking(marking, out var existing)
                && bundle.Any(part => part.BodyPart == existing.BodyPart &&
                    (part.MarkingCategory == existing.MarkingCategory || part.BodyPart is HumanoidVisualLayers.Tail or HumanoidVisualLayers.Snout)));
        else if (list.Any(marking => marking.MarkingId == selected.ID))
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
            marking.DrawOrder = order;
            var colors = MarkingColoring.GetMarkingLayerColors(part, Profile.Appearance.SkinColor, Profile.Appearance.EyeColor, set);
            for (var i = 0; i < colors.Count; i++)
                marking.SetColor(i, colors[i]);
            if (_markingManager.MustMatchSkin(Profile.Species, part.BodyPart, out var alpha, _prototypeManager))
                marking.SetColor(Profile.Appearance.SkinColor.WithAlpha(alpha));
            set.AddBack(part.MarkingCategory, marking);
            list.Add(marking);
        }
        return true;
    }

    private static int NormalizeTguiMarkingOrder(List<Marking> list)
    {
        var ordered = list.OrderByDescending(marking => marking.DrawOrder).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            ordered[i].DrawOrder = ordered.Length - i;
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
            foreach (var linked in list.Where(marking => definition.EditorLinkedMarkings.Contains(marking.MarkingId)))
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
