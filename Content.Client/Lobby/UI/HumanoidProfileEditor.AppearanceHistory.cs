using System.Linq;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private HumanoidCharacterAppearance? _appearanceBaseline;
    private HumanoidCharacterAppearance? _appearanceRedo;
    private HashSet<string> _appearanceBaselineCombine = new();
    private HashSet<string> _appearanceRedoCombine = new();

    private static HumanoidCharacterAppearance CopyAppearanceSelection(HumanoidCharacterAppearance appearance)
    {
        var copy = appearance.Clone();
        copy.Markings = appearance.Markings.Select(marking => new Marking(marking)).ToList();
        return copy;
    }

    private static bool SameAppearanceSelection(HumanoidCharacterAppearance left, HumanoidCharacterAppearance right) =>
        left.Markings.SequenceEqual(right.Markings) && left.Tattoos.Count == right.Tattoos.Count
        && left.Tattoos.Zip(right.Tattoos).All(pair => pair.First.ContentEquals(pair.Second));

    private void ResetAppearanceHistory()
    {
        _appearanceBaseline = Profile == null ? null : CopyAppearanceSelection(Profile.Appearance);
        _appearanceBaselineCombine = new(_combineMarkings);
        _appearanceRedo = null;
    }

    private bool RestoreAppearanceSelection(bool redo)
    {
        var target = redo ? _appearanceRedo : _appearanceBaseline;
        var combine = redo ? _appearanceRedoCombine : _appearanceBaselineCombine;
        if (Profile == null || target == null || _tattooFileBusy
            || SameAppearanceSelection(Profile.Appearance, target) && combine.SetEquals(_combineMarkings)) return false;
        CloseColorPicker();
        var previous = CopyAppearanceSelection(Profile.Appearance);
        var appearance = Profile.Appearance.Clone();
        var selection = appearance.WithMarkings(target.Markings.Select(marking => new Marking(marking)).ToList());
        var validated = HumanoidCharacterAppearance.EnsureValid(selection, Profile.Species, Profile.Sex, Profile.UnrestrictedMarkings);
        appearance.Markings = validated.Markings.Select(marking => new Marking(marking)).ToList();
        if (!_requirements.TattoosBanned) appearance.Tattoos = target.Tattoos.Select(tattoo => tattoo.Clone()).ToList();
        Profile = Profile.WithCharacterAppearance(appearance);
        _appearanceRedoCombine = new(_combineMarkings);
        _combineMarkings = new(combine);
        _appearanceRedo = redo ? null : previous;
        UpdateMarkings();
        ReloadProfilePreview();
        SetDirty();
        return true;
    }
}
