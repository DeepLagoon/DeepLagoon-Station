using Content.Server.Atmos.EntitySystems;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Content.Shared.Database;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private void Atmos(GridEditorAtmosRequest ev, EntitySessionEventArgs args)
    {
        var player = args.SenderSession;
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue(player, out var draft) ||
            draft.Token != ev.Token || !Exists(draft.Grid))
        {
            RaiseNetworkEvent(new GridEditorResultEvent("Нет действующей сессии Mapping.", false), player); return;
        }
        var result = EntityManager.System<AtmosphereSystem>().InspectGridEditorAtmos(draft.Grid, draft.Token, ev.Fill);
        if (ev.Fill)
            _logs.Add(LogType.AdminMessage, LogImpact.High,
                $"{player.Name} filled {result.Filled} sealed floor cells with standard air on {ToPrettyString(draft.Grid)} using grid editor");
        RaiseNetworkEvent(result, player);
    }
}
