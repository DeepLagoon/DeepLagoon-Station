using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Art;

[Serializable, NetSerializable]
public sealed class ArtModerationEntry
{
    public Guid Id;
    public string Title = "";
    public string AuthorName = "";
    public Guid Author;
    public DateTime CreatedAt;
}

[Serializable, NetSerializable]
public sealed class ArtModerationEuiState : EuiStateBase
{
    public const int PageSize = 20;
    public List<ArtModerationEntry> Entries = new();
    public int Page;
    public bool HasNext;
    public bool Loading;
    public string Status = "";
    public ArtModerationEntry? Selected;
    public string AccountName = "";
    public string Image = "";
}

[Serializable, NetSerializable]
public sealed class ArtModerationRequest(string action, Guid id = default, string search = "", int page = 0) : EuiMessageBase
{
    public string Action = action;
    public Guid Id = id;
    public string Search = search;
    public int Page = page;
}
