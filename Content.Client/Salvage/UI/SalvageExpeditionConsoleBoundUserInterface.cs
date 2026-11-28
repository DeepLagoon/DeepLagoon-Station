using Content.Client._DeepLagoon.Voyages;
using Content.Shared._DeepLagoon.Voyages;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Content.Shared.Salvage.Expeditions;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Salvage.UI;

[UsedImplicitly]
public sealed class SalvageExpeditionConsoleBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private SalvageExpeditionWindow? _window;
    private SectorExpeditionWindow? _sectorWindow;

    public SalvageExpeditionConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        if (IoCManager.Resolve<IConfigurationManager>().GetCVar(SectorExpeditionCVars.Enabled) &&
            !EntMan.GetComponent<SalvageExpeditionConsoleComponent>(Owner).DispatchByDisk)
        { _sectorWindow = EntMan.System<SectorExpeditionUiSystem>().Open(Owner); _sectorWindow.OnClose += Close; return; }
        _window = this.CreateWindowCenteredLeft<SalvageExpeditionWindow>(); // Frontier: OfferingWindow<SalvageExpeditionWindow
        _window.Title = Loc.GetString("salvage-expedition-window-title");
        // Frontier: handlers
        _window.ClaimMission += index =>
        {
            SendMessage(new ClaimSalvageMessage()
            {
                Index = index,
            });
        };
        _window.FinishMission += () => SendMessage(new FinishSalvageMessage());
        // End Frontier
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _sectorWindow != null)
        {
            _sectorWindow.OnClose -= Close;
            if (EntMan.System<SectorExpeditionUiSystem>().Window == _sectorWindow) EntMan.System<SectorExpeditionUiSystem>().Close();
            _sectorWindow = null;
        }
        base.Dispose(disposing);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not SalvageExpeditionConsoleState current)
            return;

        _window?.UpdateState(current);
    }
}
