using Content.Server.CartridgeLoader;
using Content.Shared.CartridgeLoader;
using Content.Shared.PDA;
using Content.Shared.Access.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Economy;

[RegisterComponent]
public sealed partial class MarketplaceCartridgeComponent : Component;

public sealed partial class PlayerTradeSystem
{
    private void InstallMarketplace(EntityUid uid, PdaComponent pda, CartridgeLoaderPreinstallEvent args)
    {
        if (!TryComp<CartridgeLoaderComponent>(uid, out var loader)) return;
        foreach (var program in new[] { "DLMarketplaceCartridge", "DLNotificationsCartridge" })
            if (!System.Linq.Enumerable.Contains(loader.PreinstalledPrograms, program))
                loader.PreinstalledPrograms = [..loader.PreinstalledPrograms, program];
    }
    public bool CanBrowseMarketplace(ICommonSession session, EntityUid pda)
        => Exists(pda) && !EntityManager.IsQueuedForDeletion(pda) && session.AttachedEntity is { } actor &&
            HasComp<PdaComponent>(pda) && Transform(pda).ParentUid == actor &&
            _ui.IsUiOpen(pda, PdaUiKey.Key, actor) && TryComp<CartridgeLoaderComponent>(pda, out var loader) &&
            loader.ActiveProgram is { } program && HasComp<MarketplaceCartridgeComponent>(program);

    public string MarketplaceAuthor(EntityUid pda)
        => TryComp<PdaComponent>(pda, out var device) && device.ContainedId is { } card &&
            TryComp<IdCardComponent>(card, out var id) ? id.FullName?.Trim() ?? "" : "";
}
