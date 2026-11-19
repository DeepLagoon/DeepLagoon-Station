using Content.Server.StationEvents.Events;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunGlitchRuleComponent : Component
{
    [DataField] public EntProtoId? ForcedRole;
}

public sealed class BitrunGlitchRuleSystem : StationEventSystem<BitrunGlitchRuleComponent>
{
    [Dependency] private BitrunSystem _bitrun = default!;

    protected override void Started(EntityUid uid, BitrunGlitchRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        if (!_bitrun.SpawnGlitchEvent(component.ForcedRole))
            Sawmill.Info("Bitrunning glitch event has no active domain with an unoccupied mutation candidate.");
        GameTicker.EndGameRule(uid, gameRule);
    }
}
