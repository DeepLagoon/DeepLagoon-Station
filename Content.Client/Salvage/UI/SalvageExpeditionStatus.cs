using Content.Shared.Salvage.Expeditions;
using Robust.Client.Player;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client.Salvage.UI;

public sealed class SalvageExpeditionStatus : UIWidget
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Label _label = new() { HorizontalAlignment = HAlignment.Center };
    private readonly PanelContainer _panel;

    public SalvageExpeditionStatus()
    {
        MouseFilter = MouseFilterMode.Ignore;
        _panel = new PanelContainer
        {
            Visible = false,
            MouseFilter = MouseFilterMode.Ignore,
            PanelOverride = new StyleBoxFlat(Color.FromHex("#171D29E6"))
            {
                ContentMarginLeftOverride = 12,
                ContentMarginRightOverride = 12,
                ContentMarginTopOverride = 5,
                ContentMarginBottomOverride = 5,
            },
        };
        _panel.AddChild(_label);
        AddChild(_panel);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!_entities.TryGetComponent<TransformComponent>(_players.LocalEntity, out var xform) ||
            !_entities.TryGetComponent<SalvageExpeditionComponent>(xform.MapUid, out var expedition) ||
            expedition.Stage == ExpeditionStage.Added)
        {
            _panel.Visible = false;
            return;
        }

        var seconds = Math.Max(0, (int) Math.Ceiling((expedition.EndTime - _timing.CurTime).TotalSeconds));
        _label.Text = Loc.GetString("salvage-expedition-status",
            ("time", $"{seconds / 60:00}:{seconds % 60:00}"),
            ("completed", expedition.ObjectivesCompleted), ("total", expedition.ObjectivesTotal));
        _panel.Visible = true;
    }
}
