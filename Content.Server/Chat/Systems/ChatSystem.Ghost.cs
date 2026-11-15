using Content.Shared.Administration;
using Content.Shared.Ghost;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server.Chat.Systems;

public sealed partial class ChatSystem
{
    [Dependency] private SharedContainerSystem _chatContainers = default!;

    private bool CanHearAllGhostChat(ICommonSession player, EntityUid ghost) =>
        HasComp<GhostHearingComponent>(ghost) && CanToggleGhostHearing(player, ghost);

    private bool CanToggleGhostHearing(ICommonSession player, EntityUid ghost) =>
        TryComp<GhostComponent>(ghost, out var component) && component.CanGhostInteract &&
        _adminManager.HasAdminFlag(player, AdminFlags.Admin);

    public bool TryToggleAdminGhostHearing(ICommonSession player, out bool enabled)
    {
        enabled = false;
        if (player.AttachedEntity is not { } ghost || !CanToggleGhostHearing(player, ghost))
            return false;

        if (HasComp<GhostHearingComponent>(ghost))
            RemComp<GhostHearingComponent>(ghost);
        else
        {
            AddComp<GhostHearingComponent>(ghost);
            enabled = true;
        }

        return true;
    }

    private bool CanSeeLocalGhostChat(EntityUid listener, EntityUid source, EntityCoordinates sourceCoords, float range)
    {
        if (listener == source)
            return true;

        var visibilityMask = MetaData(source).VisibilityMask;
        if (TryComp<EyeComponent>(listener, out var eye) && (eye.VisibilityMask & visibilityMask) != visibilityMask)
            return false;

        return _chatContainers.IsInSameOrTransparentContainer(listener, source, userSeeInsideSelf: true) &&
               _examineSystem.InRangeUnOccluded(listener, sourceCoords, range);
    }
}
