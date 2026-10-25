using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Player;

namespace Content.Shared._DeepLagoon.WebUI;

/// <summary>Explicit guidebook pages. A server chooses an ID, never an arbitrary URL.</summary>
[Prototype("wikiPage")]
public sealed partial class WikiPagePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string Name = default!;
    [DataField(required: true)] public string Path = default!;
    [DataField] public bool Default;
    [DataField] public List<string> GuideEntries = new();
    [DataField] public Dictionary<string, string> EntityRoutes = new();
}

[Serializable, NetSerializable]
public sealed class OpenWikiPageEvent(string pageId) : EntityEventArgs
{
    public readonly string PageId = pageId;
}
