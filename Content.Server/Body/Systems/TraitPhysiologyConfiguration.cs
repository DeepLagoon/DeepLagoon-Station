using Content.Server.Body.Components;
using Content.Shared.Body.Prototypes;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Server.Body.Systems;

public sealed partial class MetabolizerSystem
{
    public void SetTraitMetabolizers(EntityUid uid, HashSet<ProtoId<MetabolizerTypePrototype>> types)
    {
        if (TryComp<MetabolizerComponent>(uid, out var metabolizer))
            metabolizer.MetabolizerTypes = new(types);
    }
}

public sealed partial class StomachSystem
{
    public void SetTraitDigestible(EntityUid uid, EntityWhitelist whitelist)
    {
        if (TryComp<StomachComponent>(uid, out var stomach)) stomach.SpecialDigestible = whitelist;
    }
}
