using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Preferences;

namespace Content.Shared._DeepLagoon.Loadouts;

public sealed partial class PersonalLoadoutSystem
{
    public IEnumerable<(string Name, HandLocation Location)> StartingHands(HumanoidCharacterProfile profile)
    {
        if (!_prototypes.TryIndex(profile.Species, out var species) ||
            !_prototypes.Index(species.Prototype).TryGetComponent<BodyComponent>(out var body) ||
            !_prototypes.TryIndex(body.Prototype, out var prototype))
            yield break;

        foreach (var (slot, part) in prototype.Slots)
        {
            if (!_prototypes.TryIndex(part.Part, out var entity) ||
                !entity.TryGetComponent<BodyPartComponent>(out var limb) ||
                limb.PartType != BodyPartType.Hand || !limb.Enabled)
                continue;

            yield return (SharedBodySystem.GetPartSlotContainerId(slot), limb.Symmetry switch
            {
                BodyPartSymmetry.Left => HandLocation.Left,
                BodyPartSymmetry.Right => HandLocation.Right,
                _ => HandLocation.Middle,
            });
        }
    }
}
