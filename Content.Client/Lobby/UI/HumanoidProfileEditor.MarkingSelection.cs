using System.Linq;
using Content.Shared.Humanoid.Markings;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private bool TrySelectTguiMarking(List<Marking> list, MarkingPrototype selected, string points)
    {
        if (Profile == null)
            return false;

        var bundle = new List<MarkingPrototype> { selected };
        foreach (var id in selected.EditorLinkedMarkings)
        {
            if (!_markingManager.Markings.TryGetValue(id, out var linked)
                || !_markingManager.CanBeApplied(Profile.Species, Profile.Sex, linked, _prototypeManager))
                return false;
            bundle.Add(linked);
        }

        RestoreTguiBodyMarkings(list, points);

        if (selected.EditorExclusive)
            list.RemoveAll(marking => _markingManager.TryGetMarking(marking, out var existing)
                && bundle.Any(part => part.MarkingCategory == existing.MarkingCategory && part.BodyPart == existing.BodyPart));
        else if (list.Any(marking => marking.MarkingId == selected.ID))
            return false;

        var set = new MarkingSet(list, points, _markingManager, _prototypeManager);
        foreach (var part in bundle)
        {
            if (set.PointsLeft(part.MarkingCategory) == 0)
                return false;
            var marking = part.AsMarking();
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
