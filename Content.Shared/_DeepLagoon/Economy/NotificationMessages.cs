using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Economy;

[Serializable, NetSerializable]
public sealed record CharacterNoticeView(string Id, long Created, string Title, string Text, long Amount, bool Read);
[Serializable, NetSerializable]
public sealed class CharacterNotificationsSnapshot(NetEntity pda, NetEntity actor, CharacterNoticeView[] entries) : EntityEventArgs
{
    public readonly NetEntity Pda = pda, Actor = actor;
    public readonly CharacterNoticeView[] Entries = entries;
}
[Serializable, NetSerializable]
public sealed class CharacterNotificationsRefresh(NetEntity pda, bool markRead = true) : EntityEventArgs
{
    public readonly NetEntity Pda = pda;
    public readonly bool MarkRead = markRead;
}
