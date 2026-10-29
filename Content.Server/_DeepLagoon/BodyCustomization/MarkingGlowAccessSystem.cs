using Content.Server.Traits;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;

namespace Content.Server._DeepLagoon.BodyCustomization;

public sealed partial class MarkingGlowAccessSystem : EntitySystem
{
    [Dependency] private SharedDiscordBoostSystem _supporters = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawned, after: new[] { typeof(TraitSystem) });
    }

    private void OnSpawned(PlayerSpawnCompleteEvent args)
    {
        if (_supporters.CanUseMarkingGlow(args.Player) || !TryComp<HumanoidAppearanceComponent>(args.Mob, out var humanoid)) return;
        var changed = false;
        foreach (var markings in humanoid.MarkingSet.Markings.Values)
        foreach (var marking in markings)
        {
            changed |= marking.Unshaded;
            marking.Unshaded = false;
        }
        if (changed) Dirty(args.Mob, humanoid);
    }
}
