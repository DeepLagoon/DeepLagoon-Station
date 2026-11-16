using Content.Shared.Preferences.Loadouts;
using Robust.Shared.Serialization;

namespace Content.Shared.GameTicking;

[Serializable, NetSerializable]
public sealed class StartingEquipmentReviewEvent(int request, string job, int characterSlot) : EntityEventArgs
{
    public int Request { get; } = request;
    public string Job { get; } = job;
    public int CharacterSlot { get; } = characterSlot;
}

[Serializable, NetSerializable]
public sealed class StartingEquipmentReviewReply(int request, bool join, RoleLoadout? job = null, RoleLoadout? personal = null) : EntityEventArgs
{
    public int Request { get; } = request;
    public bool Join { get; } = join;
    public RoleLoadout? Job { get; } = job;
    public RoleLoadout? Personal { get; } = personal;
}
