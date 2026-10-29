using Content.Shared.Speech.Components;

namespace Content.Server.Speech.EntitySystems;

public sealed partial class VocalSystem
{
    public void RefreshTraitSounds(EntityUid uid, VocalComponent component)
    {
        LoadSounds(uid, component);
        Dirty(uid, component);
    }
}
