using Content.Client.UserInterface.Systems.Guidebook;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class WikiPageSystem : EntitySystem
{
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<OpenWikiPageEvent>(message =>
        {
            if (_prototypes.HasIndex<WikiPagePrototype>(message.PageId))
                _ui.GetUIController<GuidebookUIController>().OpenWikiPage(message.PageId);
        });
    }
}
