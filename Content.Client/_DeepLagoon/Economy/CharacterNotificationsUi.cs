using Content.Client.UserInterface.Fragments;
using Content.Shared._DeepLagoon.Economy;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.Economy;

public sealed partial class CharacterNotificationsUi : UIFragment
{
    private CharacterNotificationsView? _view;
    public override Control GetUIFragmentRoot() => _view!;
    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        if (_view is not { Disposed: false }) _view = new CharacterNotificationsView();
        IoCManager.Resolve<IEntityManager>().System<CharacterNotificationsClientSystem>().Open(userInterface.Owner, _view);
    }
    public override void UpdateState(BoundUserInterfaceState state) { }
}
public sealed class CharacterNotificationsView : BoxContainer
{
    public Action? Refresh;
    private readonly BoxContainer _rows;
    private string? _signature;
    public CharacterNotificationsView()
    {
        Orientation = LayoutOrientation.Vertical; SeparationOverride = 8;
        HorizontalExpand = VerticalExpand = true;
        var refresh = new Button { Text = "Уведомления · Обновить", HorizontalExpand = true };
        refresh.OnPressed += _ => Refresh?.Invoke(); AddChild(refresh);
        var scroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        _rows = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 10, HorizontalExpand = true };
        scroll.AddChild(_rows); AddChild(scroll);
    }
    public void Apply(CharacterNoticeView[] notices)
    {
        var signature = string.Join('|', System.Linq.Enumerable.Select(notices, n => n.Id));
        if (_signature == signature) return;
        _signature = signature;
        _rows.DisposeAllChildren();
        if (notices.Length == 0) _rows.AddChild(new Label { Text = "Уведомлений пока нет" });
        foreach (var notice in notices)
        {
            var card = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 3, HorizontalExpand = true };
            card.AddChild(new Label { Text = DateTimeOffset.FromUnixTimeMilliseconds(notice.Created).ToLocalTime().ToString("dd.MM HH:mm") + " · " + notice.Title, ClipText = true });
            var body = new RichTextLabel { HorizontalExpand = true };
            var message = new FormattedMessage(); message.AddText(notice.Text); body.SetMessage(message); card.AddChild(body);
            if (notice.Amount != 0) card.AddChild(new Label { Text = (notice.Amount > 0 ? "+" : "") + notice.Amount.ToString("N0") + " спессо",
                FontColorOverride = notice.Amount > 0 ? Color.LightGreen : Color.LightSalmon });
            _rows.AddChild(card);
        }
    }
}
public sealed partial class CharacterNotificationsClientSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _player = default!;
    private EntityUid _pda;
    private CharacterNotificationsView? _view;
    private EntityUid? _actor;
    private TimeSpan _next;
    public override void Initialize()
    {
        UpdatesOutsidePrediction = true;
        SubscribeNetworkEvent<CharacterNotificationsSnapshot>(snapshot =>
        {
            if (_view is not { Disposed: false } || snapshot.Pda != GetNetEntity(_pda) || _player.LocalEntity is not { } actor || GetNetEntity(actor) != snapshot.Actor) return;
            _view.Apply(snapshot.Entries);
        });
    }
    public void Open(EntityUid pda, CharacterNotificationsView view)
    {
        if (_pda == pda && _view == view) return;
        _pda = pda; _view = view; _next = TimeSpan.Zero;
        _actor = _player.LocalEntity;
        view.Refresh = () => { if (_view == view && view.VisibleInTree) Request(); };
    }
    private void Request()
    {
        _next = _timing.RealTime + TimeSpan.FromSeconds(10);
        RaiseNetworkEvent(new CharacterNotificationsRefresh(GetNetEntity(_pda)));
    }
    public override void Update(float frameTime)
    {
        if (_actor != _player.LocalEntity) { _actor = _player.LocalEntity; _view?.Apply([]); _next = TimeSpan.Zero; }
        if (_view is { IsInsideTree: true, VisibleInTree: true } && _timing.RealTime >= _next) Request();
    }
}
