using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.NPC;

public sealed partial class CleanbotLightReplacerSystem : EntitySystem
{
    [Dependency] private SharedPoweredLightSystem _poweredLight = default!;
    [Dependency] private SharedLightBulbSystem _bulb = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    public bool CanReplace(EntityUid bot, EntityUid fixture,
        CleanbotLightReplacerComponent? replacer = null, PoweredLightComponent? light = null)
    {
        if (!Resolve(bot, ref replacer, false) || !Resolve(fixture, ref light, false))
            return false;

        return GetBrokenBulb(fixture, light) is { } broken && TryGetReplacement(broken, out _);
    }

    public bool TryReplace(EntityUid bot, EntityUid fixture,
        CleanbotLightReplacerComponent? replacer = null, PoweredLightComponent? light = null)
    {
        if (!Resolve(bot, ref replacer, false) || !Resolve(fixture, ref light, false))
            return false;

        if (GetBrokenBulb(fixture, light) is not { } broken)
            return false;

        if (!TryGetReplacement(broken, out var replacement))
            return false;

        var coordinates = Transform(fixture).Coordinates;

        if (_poweredLight.EjectBulb(fixture, null, light) is { } ejected)
            Del(ejected);

        var bulb = Spawn(replacement, coordinates);

        if (TryComp<LightBulbComponent>(bulb, out var bulbComp))
            _bulb.SetState(bulb, LightBulbState.Normal, bulbComp);

        if (!_poweredLight.InsertBulb(fixture, bulb, light))
        {
            Del(bulb);
            return false;
        }

        _audio.PlayPvs(replacer.ReplaceSound, fixture);
        return true;
    }

    public EntityUid? GetBrokenBulb(EntityUid fixture, PoweredLightComponent? light = null)
    {
        if (!Resolve(fixture, ref light, false))
            return null;

        if (_poweredLight.GetBulb(fixture, light) is not { } bulb)
            return null;

        return TryComp<LightBulbComponent>(bulb, out var bulbComp) && bulbComp.State != LightBulbState.Normal
            ? bulb
            : null;
    }

    private bool TryGetReplacement(EntityUid broken, out EntProtoId replacement)
    {
        if (MetaData(broken).EntityPrototype is { } prototype)
        {
            replacement = prototype.ID;
            return true;
        }

        replacement = default;
        return false;
    }
}
