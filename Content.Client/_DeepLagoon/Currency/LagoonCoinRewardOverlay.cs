using System.Numerics;
using Content.Client.Resources;
using Content.Shared._DeepLagoon.Currency;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Currency;

public sealed partial class LagoonCoinRewardOverlay : Overlay
{
    [Dependency] private IResourceCache _cache = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Queue<LagoonCoinRewardEvent> _pending = new();
    private readonly Font _titleFont;
    private readonly Font _detailFont;
    private string? _title;
    private string _detail = "";
    private TimeSpan _started;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);
    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public LagoonCoinRewardOverlay()
    {
        IoCManager.InjectDependencies(this);
        var fonts = new[] { "/Fonts/Iceberg/Iceberg-Regular.ttf", "/Fonts/NotoSansDisplay/NotoSansDisplay-Regular.ttf" };
        _titleFont = _cache.GetFont(fonts, 25);
        _detailFont = _cache.GetFont(fonts, 15);
    }

    public void Enqueue(LagoonCoinRewardEvent reward) => _pending.Enqueue(reward);

    public void Clear()
    {
        _pending.Clear();
        _title = null;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_title != null && _timing.RealTime - _started >= Duration)
            _title = null;
        if (_title == null)
        {
            if (!_pending.TryDequeue(out var reward))
                return;
            _title = Loc.GetString("lagoon-coin-reward-title", ("amount", reward.Amount));
            var reason = reward.Reason switch
            {
                "ready" => "lagoon-coin-reason-ready",
                "playtime" => "lagoon-coin-reason-playtime",
                "antagonist-objective" => "lagoon-coin-reason-objective",
                _ => "lagoon-coin-reason-other",
            };
            _detail = Loc.GetString("lagoon-coin-reward-detail",
                ("reason", Loc.GetString(reason)), ("balance", reward.Balance));
            _started = _timing.RealTime;
        }

        var elapsed = (_timing.RealTime - _started).TotalSeconds;
        var alpha = (float) Math.Clamp(Math.Min(elapsed / 0.3, (Duration.TotalSeconds - elapsed) / 0.6), 0, 1);
        DrawCentered(args, _titleFont, _title, 260, new Color(0.4f, 0.9f, 0.8f, alpha));
        DrawCentered(args, _detailFont, _detail, 295, new Color(1f, 1f, 1f, alpha));
    }

    private static void DrawCentered(in OverlayDrawArgs args, Font font, string text, float y, Color color)
    {
        var width = 0f;
        foreach (var rune in text.EnumerateRunes())
        {
            if (font.TryGetCharMetrics(rune, 1, out var metrics))
                width += metrics.Advance;
        }
        args.ScreenHandle.DrawString(font, new Vector2((args.ViewportBounds.Width - width) / 2, y), text, color);
    }
}
