using Content.Shared._DeepLagoon.WebUI;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.WebUI;

public sealed partial class WikiPageSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    /// <summary>Opens an explicitly configured document, always in the guidebook window.</summary>
    public void OpenPage(ICommonSession session, ProtoId<WikiPagePrototype> page)
    {
        if (_prototypes.HasIndex(page))
            RaiseNetworkEvent(new OpenWikiPageEvent(page), session.Channel);
    }
}
