using Content.Shared._NF.CCVar;
using Content.Shared.Salvage.Expeditions;
using Robust.Client.Player;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Client.Salvage.UI;

public sealed class SalvageExpeditionStatus : UIWidget
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IConfigurationManager _config = default!;

    private readonly Label _label = new() { HorizontalAlignment = HAlignment.Center };
    private readonly Label _reward = new() { HorizontalAlignment = HAlignment.Center };
    private readonly Label _receipt = new() { HorizontalAlignment = HAlignment.Center };
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
        _panel.AddChild(new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MouseFilter = MouseFilterMode.Ignore,
            Children = { _label, _reward, _receipt },
        });
        AddChild(_panel);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        _receipt.Text = _entities.System<SalvageSystem>().ExpeditionRewardNotice;
        _receipt.Visible = _receipt.Text != null;
        if (!_entities.TryGetComponent<TransformComponent>(_players.LocalEntity, out var xform) ||
            !_entities.TryGetComponent<SalvageExpeditionComponent>(xform.MapUid, out var expedition) ||
            expedition.Stage == ExpeditionStage.Added)
        {
            _label.Visible = false;
            _reward.Visible = false;
            _panel.Visible = _receipt.Visible;
            return;
        }

        var seconds = Math.Max(0, (int) Math.Ceiling((expedition.EndTime - _timing.CurTime).TotalSeconds));
        _label.Text = Loc.GetString("salvage-expedition-status",
            ("time", $"{seconds / 60:00}:{seconds % 60:00}"),
            ("completed", expedition.ObjectivesCompleted), ("total", expedition.ObjectivesTotal));
        _label.Visible = true;
        _reward.Visible = expedition.RewardAmount > 0;
        if (_reward.Visible)
        {
            if (!expedition.DispatchedFromOutpost && !_config.GetCVar(NFCCVars.SalvageExpeditionRewardsEnabled))
                _reward.Text = Loc.GetString("salvage-expedition-reward-disabled");
            else
                _reward.Text = Loc.GetString(expedition.DispatchedFromOutpost
                    ? "salvage-expedition-reward-expected-outpost"
                    : "salvage-expedition-reward-expected-cash", ("amount", expedition.RewardAmount));
        }
        _panel.Visible = true;
    }
}
