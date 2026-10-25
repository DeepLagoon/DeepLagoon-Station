using Content.Client.Resources;
using Content.Shared._DeepLagoon.Currency;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Currency;

public sealed class LagoonCoinRewardNotification : Control
{
    [Dependency] private IResourceCache _cache = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Queue<LagoonCoinRewardEvent> _pending = new();
    private readonly PanelContainer _panel;
    private readonly Label _title;
    private readonly Label _detail;
    private TimeSpan _started;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);

    public LagoonCoinRewardNotification()
    {
        IoCManager.InjectDependencies(this);
        Name = "LagoonCoinRewardNotification";
        MouseFilter = MouseFilterMode.Ignore;
        var fonts = new[] { "/Fonts/Iceberg/Iceberg-Regular.ttf", "/Fonts/NotoSansDisplay/NotoSansDisplay-Regular.ttf" };
        _title = new Label
        {
            Name = "RewardTitle",
            FontOverride = _cache.GetFont(fonts, 25),
            FontColorOverride = new Color(0.4f, 0.9f, 0.8f),
            HorizontalAlignment = HAlignment.Center,
            MouseFilter = MouseFilterMode.Ignore,
        };
        _detail = new Label
        {
            Name = "RewardDetail",
            FontOverride = _cache.GetFont(fonts, 15),
            HorizontalAlignment = HAlignment.Center,
            MouseFilter = MouseFilterMode.Ignore,
        };
        var content = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, MouseFilter = MouseFilterMode.Ignore };
        content.AddChild(_title);
        content.AddChild(_detail);
        _panel = new PanelContainer
        {
            Name = "RewardPanel",
            Visible = false,
            MouseFilter = MouseFilterMode.Ignore,
            PanelOverride = new StyleBoxFlat(Color.FromHex("#172A32ED"))
            {
                ContentMarginLeftOverride = 18,
                ContentMarginRightOverride = 18,
                ContentMarginTopOverride = 8,
                ContentMarginBottomOverride = 8,
            },
        };
        _panel.AddChild(content);
        AddChild(_panel);
    }

    public void Enqueue(LagoonCoinRewardEvent reward)
    {
        if (reward.Amount != 0)
            _pending.Enqueue(reward);
    }

    public void Clear()
    {
        _pending.Clear();
        _panel.Visible = false;
    }

    public void Update()
    {
        var now = _timing.RealTime;
        if (_panel.Visible && now - _started >= Duration)
            _panel.Visible = false;
        if (!_panel.Visible)
        {
            if (!_pending.TryDequeue(out var reward))
                return;
            var title = reward.Amount > 0 ? "lagoon-coin-reward-title" : "lagoon-coin-debit-title";
            _title.Text = Loc.GetString(title, ("amount", reward.Amount));
            _title.FontColorOverride = reward.Amount > 0
                ? new Color(0.4f, 0.9f, 0.8f)
                : new Color(1f, 0.6f, 0.5f);
            var reason = reward.Reason switch
            {
                "ready" => Loc.GetString("lagoon-coin-reason-ready"),
                "playtime" => Loc.GetString("lagoon-coin-reason-playtime"),
                "antagonist-objective" => Loc.GetString("lagoon-coin-reason-objective"),
                _ => reward.Reason,
            };
            _detail.Text = Loc.GetString("lagoon-coin-reward-detail",
                ("reason", reason), ("balance", reward.Balance));
            _started = now;
            _panel.Visible = true;
        }
        var elapsed = (now - _started).TotalSeconds;
        var alpha = (float) Math.Clamp(Math.Min(elapsed / 0.3, (Duration.TotalSeconds - elapsed) / 0.6), 0, 1);
        _panel.Modulate = Color.White.WithAlpha(alpha);
    }
}
