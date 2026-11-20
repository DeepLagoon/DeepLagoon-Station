using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Content.Client.Administration.Managers;
using Content.Client.Administration.Systems;
using Content.Client.Administration.UI;
using Content.Client.Administration.UI.CustomControls;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared.Administration;
using Content.Shared._DeepLagoon.Ahelp;
using Robust.Client.Console;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.Ahelp;

public sealed class AhelpWindow : FancyWindow
{
    private sealed record Upload(AhelpRequest Request, byte[] Data)
    {
        public int Offset;
        public GameTick Tick;
    }

    private sealed record Pending(AhelpRequest Request, byte[]? Data, TimeSpan Deadline)
    {
        public string Error = "";
    }

    private readonly BwoinkSystem _network;
    private readonly IClientAdminManager _admin = IoCManager.Resolve<IClientAdminManager>();
    private readonly IClientConsoleHost _console = IoCManager.Resolve<IClientConsoleHost>();
    private readonly IPlayerManager _players = IoCManager.Resolve<IPlayerManager>();
    private readonly IFileDialogManager _files = IoCManager.Resolve<IFileDialogManager>();
    private readonly IGameTiming _timing = IoCManager.Resolve<IGameTiming>();
    private readonly IInputManager _input = IoCManager.Resolve<IInputManager>();
    private readonly Guid _actor;
    private readonly bool _isAdmin;
    private readonly BoxContainer _body = new() { VerticalExpand = true, HorizontalExpand = true, SeparationOverride = 8 };
    private readonly BoxContainer _sidebar = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = 235, MaxWidth = 235, SeparationOverride = 5 };
    private readonly BoxContainer _rows = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 3 };
    private readonly BoxContainer _thread = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 5 };
    private readonly BoxContainer _navigation = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = 190, MaxWidth = 190, SeparationOverride = 8 };
    private readonly BoxContainer _toolbar = new() { SeparationOverride = 3 };
    private readonly BoxContainer _playerActions = new() { SeparationOverride = 3 };
    private readonly Button _follow;
    private readonly BoxContainer _native = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, VerticalExpand = true, Visible = false, SeparationOverride = 6 };
    private readonly BoxContainer _nativeLines = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 6 };
    private readonly TicketHistoryScroll _nativeHistory = new() { VerticalExpand = true, HScrollEnabled = false };
    private readonly VScrollBar _nativeScroll;
    private readonly PlainTextLabel _header = new();
    private readonly PlainTextLabel _status = new();
    private readonly PlainTextLabel _typingLabel = new();
    private TimeSpan _lastTypingInput;
    private TimeSpan _nextTyping;
    private TimeSpan _typingExpires;
    private Guid _typingTicket;
    private bool _typingStaff;
    private bool _typingInput;
    private readonly LineEdit _draft = new() { HorizontalExpand = true, PlaceHolder = "Сообщение" };
    private readonly LineEdit _reason = new() { PlaceHolder = "Причина отклонения", HorizontalExpand = true, Visible = false };
    private readonly LineEdit _search = new() { PlaceHolder = "Поиск" };
    private readonly CheckBox _staff = new() { Text = "Только администраторам" };
    private readonly CheckBox _sound = new() { Text = "Bwoink?", Pressed = true };
    private readonly TguiPanel _web = new(inheritChatAppearance: false, showWindowControls: false);
    private readonly SortedDictionary<long, AhelpLine> _lines = new();
    private readonly Dictionary<long, AhelpThumbnailData> _thumbnails = new();
    private readonly Dictionary<long, AhelpThumbnail> _thumbnailTextures = new();
    private readonly HashSet<long> _thumbnailUnavailable = new();
    private readonly Button _nativeOlder;
    private readonly Button _nativeFile;
    private readonly Button _nativeSend;
    private readonly Button _nativeLatest;
    private readonly HashSet<Guid> _pinned = new();
    private readonly Dictionary<Button, ConfirmationData> _confirmations = new();
    private AhelpTicket[] _tickets = [];
    private AhelpTicket? _selected;
    private Guid _ticket;
    private Guid _target;
    private Guid _operation;
    private Guid _readId;
    private AhelpRequest? _retryRequest;
    private byte[]? _retryBytes;
    private TimeSpan _deadline;
    private TimeSpan _nextRead;
    private bool _refresh;
    private bool _reading;
    private bool _older;
    private long _before;
    private int _filter;
    private bool _disposed;
    private readonly Dictionary<Guid, Upload> _uploads = new();
    private readonly Dictionary<Guid, Pending> _pending = new();
    private StringBuilder? _paste;
    private (Guid Ticket, Guid Player, string Round) _pasteTarget;
    private string _round = "";
    private string _pasteFile = "";
    private long _downloadId;
    private byte[]? _download;
    private bool[]? _downloadChunks;
    private int _received;
    private bool _save;
    private bool _downloadingThumbnail;
    private TimeSpan _downloadDeadline;
    private TimeSpan _nextThumbnail;
    private (long Id, bool Save)? _requestedFile;
    private string _preview = "";
    private long _previewId;
    private int _sent;
    private AhelpTeleportState? _navigationState;
    private Guid _navigationTarget;
    private TimeSpan _nextNavigation;
    private IClydeWindow? _desktop;
    private WindowRoot? _desktopRoot;
    public event Action? TicketClosed;

    public AhelpWindow(BwoinkSystem network, Guid actor, bool admin)
    {
        _network = network;
        _actor = actor;
        _isAdmin = admin;
        Title = "Ahelp";
        MinSize = new Vector2(admin ? 980 : 510, 450);
        SetSize = new Vector2(admin ? 1150 : 640, 600);
        ContentsContainer.AddChild(Panel("#242424", _body, 8));
        if (admin)
        {
            _body.AddChild(Panel("#242424", _sidebar, 6));
            var filters = new BoxContainer { SeparationOverride = 3 };
            foreach (var (label, index) in new[] { ("Новые", 0), ("Мои", 1), ("В работе", 2), ("Игроки", 3) })
                filters.AddChild(Button(label, () => { _filter = index; PopulateQueue(); }));
            _sidebar.AddChild(filters);
            _sidebar.AddChild(_search);
            _search.OnTextChanged += _ => PopulateQueue();
            _sidebar.AddChild(new ScrollContainer { VerticalExpand = true, Children = { _rows } });
        }
        _body.AddChild(_thread);
        if (admin)
            _body.AddChild(Panel("#292929", _navigation, 6));
        _thread.AddChild(Panel("#292929", _header, 7));
        _thread.AddChild(_toolbar);
        _thread.AddChild(_playerActions);
        _follow = Button("Следовать", () => _console.ExecuteCommand($"followuser \"{_selected?.Player ?? _target}\""));
        _follow.HorizontalExpand = true;
        _thread.AddChild(_follow);
        _thread.AddChild(_reason);
        _reason.OnTextEntered += _ =>
        {
            if (!string.IsNullOrWhiteSpace(_reason.Text) && _reason.Text.Length <= AhelpLimits.Text)
            {
                Mutate(AhelpAction.Reject, _reason.Text);
                _reason.Visible = false;
            }
        };
        _thread.AddChild(_web);
        _thread.AddChild(_native);
        _thread.AddChild(_typingLabel);
        _thread.AddChild(_status);
        _nativeOlder = Button("Ранние сообщения", Older);
        _nativeFile = Button("Файл", ChooseFile);
        _nativeSend = Button("Отправить", SendDraft);
        _nativeOlder.HorizontalAlignment = HAlignment.Left;
        _native.AddChild(_nativeOlder);
        _nativeScroll = _nativeHistory.Children.OfType<VScrollBar>().Single();
        _nativeHistory.AddChild(_nativeLines);
        _nativeLatest = Button("К последнему сообщению ↓", JumpToLatest);
        _nativeLatest.HorizontalAlignment = HAlignment.Right;
        _nativeLatest.VerticalAlignment = VAlignment.Bottom;
        _nativeLatest.Margin = new Thickness(0, 0, 16, 7);
        _nativeLatest.Visible = false;
        var timeline = Panel("#242424", _nativeHistory, 5);
        timeline.AddChild(_nativeLatest);
        _native.AddChild(timeline);
        _nativeHistory.OnScrolled += UpdateLatestButton;
        _native.AddChild(Panel("#292929", new BoxContainer
        {
            SeparationOverride = 5,
            Children = { _draft, _nativeFile, _nativeSend }
        }, 5));
        if (admin)
            _thread.AddChild(new BoxContainer { Children = { _staff, _sound } });
        _staff.OnToggled += args =>
        {
            StopTyping();
            _sound.Disabled = args.Pressed;
            Render();
        };
        _draft.OnTextEntered += _ => SendDraft();
        _draft.OnTextChanged += _ => TypingInput(_draft.Text.Length > 0 && _draft.HasKeyboardFocus());
        _web.SetNativeFallback(() => { _native.Visible = true; _web.Visible = false; Render(); FocusPlayerComposer(); });
        _web.SetTguiRestore(() => { _native.Visible = false; _web.Visible = true; Render(); FocusPlayerComposer(); });
        _web.Web.Ready += FocusPlayerComposer;
        _web.OnAction += OnWebAction;
        _input.FirstChanceOnKeyEvent += OnInput;
        _network.TicketSnapshot += OnState;
        _network.TicketNotice += OnNotice;
        _network.TicketDownload += OnDownload;
        _network.TicketTeleportState += OnTeleportState;
        _network.TicketTyping += OnTyping;
        _network.TicketPlayerAction += OnPlayerAction;
        OnClose += () => { if (_desktop == null) Dispose(); };
        Render();
    }

    private static PanelContainer Panel(string background, Control content, float padding) => new()
    {
        HorizontalExpand = content.HorizontalExpand,
        VerticalExpand = content.VerticalExpand,
        PanelOverride = new StyleBoxFlat(Color.FromHex(background))
        {
            ContentMarginLeftOverride = padding, ContentMarginRightOverride = padding,
            ContentMarginTopOverride = padding, ContentMarginBottomOverride = padding,
        },
        Children = { content }
    };

    private void FocusPlayerComposer()
    {
        if (_isAdmin || _disposed || !IsOpen || !IsAtFront() || !CanWrite)
            return;
        if (_native.Visible)
            _draft.GrabKeyboardFocus();
        else if (_web.Web.IsReady)
            _web.Web.FocusTextInput();
    }

    private void OnInput(KeyEventArgs args, KeyEventType type)
    {
        if (args.Handled || _disposed)
            return;
        if (args.Key == Keyboard.Key.MouseLeft && type == KeyEventType.Up && !_isAdmin && IsOpen)
        {
            for (var hit = UserInterfaceManager.MouseGetControl(_input.MouseScreenPosition); hit != null; hit = hit.Parent)
            {
                if (hit != this) continue;
                UserInterfaceManager.DeferAction(FocusPlayerComposer);
                break;
            }
            return;
        }
        if (type == KeyEventType.Up || args.Key is not (Keyboard.Key.Return or Keyboard.Key.NumpadEnter))
            return;
        if (_native.Visible && _draft.HasKeyboardFocus())
        {
            args.Handle();
            if (type == KeyEventType.Down)
                SendDraft();
        }
        else if (!WebViewCapabilities.NativeLifecycle && _web.Web is { IsReady: true, HasInputFocus: true })
        {
            args.Handle();
            if (type == KeyEventType.Down)
                _web.Web.SendInputKey(false, args.Shift);
        }
    }

    public void FocusPlayer(Guid player)
    {
        if (_operation != Guid.Empty) return;
        if (_ticket != Guid.Empty || _target != player)
            ResetThread();
        _ticket = Guid.Empty;
        _target = player;
        _refresh = true;
    }

    private void ResetThread()
    {
        StopTyping();
        _typingLabel.Text = "";
        _reading = _older = false;
        _readId = default;
        _lines.Clear();
        _before = 0;
        _selected = null;
        _preview = "";
        _requestedFile = null;
        ResetDownload();
        ClearThumbnails();
        _reason.Visible = false;
        _status.Text = "";
        JumpToLatest();
    }

    private static Button Button(string title, Action action, bool disabled = false)
    {
        var control = new Button { Text = title, Disabled = disabled };
        control.OnPressed += _ => action();
        return control;
    }

    private void PopulateQueue()
    {
        if (!_isAdmin) return;
        _rows.DisposeAllChildren();
        var filter = _search.Text.Trim();
        if (_filter == 3)
        {
            foreach (var player in _players.Sessions.OrderByDescending(p => _pinned.Contains(p.UserId.UserId)).ThenBy(p => p.Name))
            {
                if (player.UserId.UserId == _actor || !player.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var id = player.UserId.UserId;
                _rows.AddChild(Button((_pinned.Contains(id) ? "◆ " : "") + player.Name, () => FocusPlayer(id)));
            }
            return;
        }
        foreach (var ticket in _tickets)
        {
            if (_filter == 0 && ticket.Status != AhelpStatus.New ||
                _filter == 1 && ticket.Assignee != _actor ||
                _filter == 2 && ticket.Status == AhelpStatus.New)
                continue;
            if (!ticket.PlayerName.Contains(filter, StringComparison.OrdinalIgnoreCase) && !ticket.Number.ToString().Contains(filter))
                continue;
            var age = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ticket.Created) / 60);
            var label = $"#{ticket.Number} {ticket.PlayerName} · {age} мин";
            if (ticket.AssigneeName != null) label += $" · {ticket.AssigneeName}";
            if (ticket.Unread) label = "● " + label;
            _rows.AddChild(Button(label, () =>
            {
                if (_operation != Guid.Empty) return;
                if (_ticket != ticket.Id) ResetThread();
                _ticket = ticket.Id;
                _target = ticket.Player;
                _refresh = true;
            }));
        }
    }

    private bool CanWrite => _round.Length > 0 && (_selected == null ? !_isAdmin || _target != Guid.Empty :
        _selected.Status < AhelpStatus.Closing &&
        (_isAdmin && (_staff.Pressed || _selected.Status == AhelpStatus.New) || _selected.Player == _actor || _selected.Assignee == _actor));

    private void Render()
    {
        PopulateQueue();
        _header.Text = _selected is { } ticket
            ? $"#{ticket.Number} · {ticket.PlayerName} · {Status(ticket.Status)}" +
              (ticket.AssigneeName == null ? "" : $" · {ticket.AssigneeName}")
            : _target == Guid.Empty ? (_isAdmin ? "Тикеты ahelp" : "Новое обращение")
            : _players.SessionsDict.GetValueOrDefault(new NetUserId(_target))?.Name ?? "Игрок";
        _toolbar.DisposeAllChildren();
        _playerActions.DisposeAllChildren();
        _confirmations.Clear();
        RenderNavigation();
        _follow.Visible = _isAdmin;
        _follow.Disabled = !_admin.CanCommand("follow") || (_selected?.Player ?? _target) == Guid.Empty;
        if (_isAdmin)
        {
            if (_desktop == null)
                _toolbar.AddChild(Button("Отдельное окно", PopOut));
            if (_selected != null)
            {
                var mine = _selected.Assignee == _actor && _selected.Status == AhelpStatus.Claimed;
                _toolbar.AddChild(Button("Взять", () => Mutate(AhelpAction.Claim), _selected.Status != AhelpStatus.New || _selected.Player == _actor));
                _toolbar.AddChild(Button("Решить", () => Mutate(AhelpAction.Resolve), !mine));
                _toolbar.AddChild(Button("Отклонить", () =>
                {
                    _reason.Visible = true;
                    _reason.GrabKeyboardFocus();
                }, !mine));
                _toolbar.AddChild(Button("Закрыть", () => Mutate(AhelpAction.Close), !mine));
            }
            PlayerActions();
        }
        if (_selected is { } own && own.Player == _actor && own.Status < AhelpStatus.Closing)
            _toolbar.AddChild(Button("Закрыть обращение", () => Mutate(AhelpAction.Close), _operation != Guid.Empty));
        if (_native.Visible)
        {
            RenderNativeHistory();
            return;
        }
        RenderWebHistory();
    }

    private void RenderNativeHistory()
    {
        _nativeOlder.Visible = _before > 0;
        _draft.Editable = CanWrite;
        _nativeFile.Disabled = _nativeSend.Disabled = !CanWrite;
        var follow = _nativeScroll.IsAtEnd;
        _nativeLines.DisposeAllChildren();
        foreach (var line in _lines.Values)
        {
            var card = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 4 };
            card.AddChild(WrappedText($"{line.AuthorName}  ·  {Clock(line.At)}{(line.Staff ? "  ·  для администраторов" : "")}", LineColor(line)));
            if (line.Text.Length > 0)
            {
                var label = new RichTextLabel { HorizontalExpand = true };
                var message = new FormattedMessage();
                message.AddText(line.Text);
                label.SetMessage(message);
                card.AddChild(label);
            }
            if (line.File != null)
            {
                if (line.Mime == AhelpLimits.Loading)
                    card.AddChild(new LoadingLabel(_timing, "Загрузка медиа…"));
                else if (line.Mime == AhelpLimits.Failed)
                    card.AddChild(new Label { Text = "Не удалось загрузить медиа.", FontColorOverride = Color.Orange });
                else if (GetThumbnail(line.Id) is { } thumbnail)
                    card.AddChild(thumbnail.CreateControl());
                else if (line.Mime?.StartsWith("image/", StringComparison.Ordinal) == true && !_thumbnailUnavailable.Contains(line.Id))
                    card.AddChild(new LoadingLabel(_timing, "Загрузка медиа…"));
                card.AddChild(WrappedText(line.File));
                var save = Button("Сохранить", () => RequestFile(line.Id, true), line.Mime is AhelpLimits.Loading or AhelpLimits.Failed);
                card.AddChild(save);
            }
            _nativeLines.AddChild(Panel("#242424", card, 3));
        }
        foreach (var pending in CurrentPending())
        {
            var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
            row.AddChild(WrappedText(pending.Request.Text + (pending.Request.File.Length > 0 ? " · " + pending.Request.File : "")));
            row.AddChild(WrappedText(pending.Error.Length == 0 ? "◌ Отправка…" : Error(pending.Error)));
            if (pending.Error.Length > 0)
                row.AddChild(Button("Повторить", () => Start(pending.Request, pending.Data)));
            _nativeLines.AddChild(row);
        }
        if (_lines.Count == 0)
            _nativeLines.AddChild(new Label { Text = "Сообщений пока нет", FontColorOverride = Color.FromHex("#A0B1C6") });
        if (follow)
            _nativeHistory.FollowLatest();
        UpdateLatestButton();
    }

    private void JumpToLatest()
    {
        _nativeHistory.FollowLatest();
        _nativeLatest.Visible = false;
    }

    private void UpdateLatestButton()
    {
        if (!_disposed)
            _nativeLatest.Visible = !_nativeHistory.JumpPending && !_nativeScroll.IsAtEnd;
    }

    internal sealed class TicketHistoryScroll : ScrollContainer
    {
        public TicketHistoryScroll()
        {
            HScrollEnabled = false;
            HorizontalExpand = true;
        }

        public bool JumpPending { get; private set; }

        public void FollowLatest()
        {
            JumpPending = true;
            InvalidateArrange();
        }

        protected override Vector2 ArrangeOverride(Vector2 finalSize)
        {
            var arranged = base.ArrangeOverride(finalSize);
            if (!JumpPending)
                return arranged;

            JumpPending = false;
            var previousOffset = GetScrollValue();
            VScroll = float.MaxValue;

            // Changing the scrollbar during layout cannot invalidate this pass.
            return GetScrollValue() != previousOffset ? base.ArrangeOverride(finalSize) : arranged;
        }
    }

    private sealed class PlainTextLabel : RichTextLabel
    {
        public PlainTextLabel() => HorizontalExpand = true;
        public new string Text
        {
            set
            {
                var message = new FormattedMessage();
                message.AddText(value);
                SetMessage(message);
            }
        }
    }

    private sealed class LoadingLabel(IGameTiming timing, string caption) : Label
    {
        private static readonly string[] Frames = ["◐", "◓", "◑", "◒"];
        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);
            Text = Frames[(int) (timing.RealTime.TotalSeconds * 8) % Frames.Length] + " " + caption;
        }
    }

    private void RenderWebHistory()
    {
        _web.SetIncrementalState("AhelpTicket", new TguiData().Bool("writable", CanWrite)
            .Bool("focusComposer", !_isAdmin).Bool("busy", _operation != Guid.Empty).Bool("older", _before > 0).Number("sent", _sent)
            .Bool("retry", _operation == Guid.Empty && _retryRequest is { } retry && retry.Ticket == _ticket && retry.Player == _target)
            .String("round", _round)
            .String("ticket", (_isAdmin ? _target.ToString() : "self") + "/" + _round).String("preview", _preview).Number("previewId", _previewId)
            .Array("thumbnails", _thumbnails.Select(entry => entry.Value.ToWeb(entry.Key)))
            .Array("pending", CurrentPending().Select(pending => new TguiData().String("id", pending.Request.Operation.ToString())
                .String("text", pending.Request.Text).String("file", pending.Request.File).String("error", pending.Error.Length == 0 ? "" : Error(pending.Error))))
            .Array("lines", _lines.Values.Select(line => new TguiData().Number("id", line.Id).String("at", Clock(line.At))
                .String("author", line.AuthorName).String("color", LineColor(line).ToHex()).String("text", line.Text).String("file", line.File)
                .Bool("loading", line.Mime == AhelpLimits.Loading).Bool("failed", line.Mime == AhelpLimits.Failed)
                .String("mime", line.Mime).Bool("previewUnavailable", _thumbnailUnavailable.Contains(line.Id) ||
                    line.Mime != null && !line.Mime.StartsWith("image/", StringComparison.Ordinal)).Bool("staff", line.Staff))));
    }

    private IEnumerable<Pending> CurrentPending() => _pending.Values.Where(pending =>
        pending.Request.Action == AhelpAction.Send && pending.Request.Player == _target &&
        (pending.Request.Ticket == _ticket || pending.Request.Ticket == Guid.Empty));

    internal static RichTextLabel WrappedText(string text, Color? color = null)
    {
        var label = new RichTextLabel { HorizontalExpand = true };
        var message = new FormattedMessage();
        message.AddText(text);
        label.SetMessage(message, color);
        return label;
    }

    private void OnTeleportState(AhelpTeleportState state)
    {
        if (state.Player != (_selected?.Player ?? _target)) return;
        _navigationState = state;
        if (state.Result.Length > 0) _status.Text = state.Result;
        RenderNavigation();
    }

    private void RenderNavigation()
    {
        if (!_isAdmin) return;
        var target = _selected?.Player ?? _target;
        if (_navigationTarget != target)
        {
            _navigationTarget = target;
            _navigationState = null;
            _nextNavigation = TimeSpan.Zero;
        }
        _navigation.DisposeAllChildren();
        _navigation.AddChild(WrappedText("Телепортация"));
        if (target == Guid.Empty)
        {
            _navigation.AddChild(WrappedText("Выберите игрока."));
            return;
        }
        foreach (var (kind, title, error) in new[]
        {
            ("ship", "Корабль", _navigationState?.ShipError),
            ("body", "Тело", _navigationState?.BodyError),
            ("ghost", "Призрак", _navigationState?.GhostError),
        })
        {
            if (error != "")
            {
                _navigation.AddChild(WrappedText((kind == "ship" ? "Корабль: " : kind == "body" ? "Тело: " : "Призрак: ") +
                    (error ?? "Проверка целей…"), Color.Gray));
                continue;
            }
            var button = Button("", () => _network.SendTeleport(new AhelpTeleportRequest(target, kind)));
            button.HorizontalExpand = true;
            button.AddChild(WrappedText(title));
            _navigation.AddChild(button);
        }
        _navigation.AddChild(WrappedText("Действия"));
        foreach (var (action, title, flag) in new[]
        {
            ("vv", "VV игрока", AdminFlags.VarEdit),
            ("time", "Перенос времен", AdminFlags.Moderator),
            ("logs", "Логи игрока", AdminFlags.Logs),
        })
        {
            var button = Button(title, () => _network.SendPlayerAction(new AhelpPlayerActionRequest(target, action)), !_admin.HasFlag(flag));
            button.HorizontalExpand = true;
            _navigation.AddChild(button);
        }
    }

    private void OnPlayerAction(AhelpPlayerActionResult state)
    {
        if (state.Player != (_selected?.Player ?? _target)) return;
        if (state.Error.Length > 0) _status.Text = state.Error;
        else if (state.Entity is { } entity) _console.ExecuteCommand($"vv {entity}");
    }

    private Color LineColor(AhelpLine line) => line.Author == Guid.Empty ? Color.Gray :
        line.Author == (_selected?.Player ?? _actor) ? Color.White : line.Staff ? Color.Purple : Color.Red;

    private AhelpThumbnail? GetThumbnail(long id)
    {
        if (_thumbnailTextures.TryGetValue(id, out var existing))
            return existing;
        if (!_thumbnails.TryGetValue(id, out var image))
            return null;
        try
        {
            var texture = AhelpThumbnail.Load(image, IoCManager.Resolve<IClyde>(), _timing);
            _thumbnailTextures.Add(id, texture);
            return texture;
        }
        catch (Exception) { return null; }
    }

    private void ClearThumbnails()
    {
        _nativeLines.DisposeAllChildren();
        foreach (var texture in _thumbnailTextures.Values)
            texture.Dispose();
        _thumbnailTextures.Clear();
        _thumbnails.Clear();
        _thumbnailUnavailable.Clear();
    }

    private void PlayerActions()
    {
        var target = _selected?.Player ?? _target;
        if (target == Guid.Empty)
            return;
        var row = _playerActions;
        row.AddChild(Button(_pinned.Contains(target) ? "Открепить" : "Закрепить", () =>
        {
            if (!_pinned.Add(target)) _pinned.Remove(target);
            Render();
        }));
        if (_admin.HasFlag(AdminFlags.Ban))
        {
            row.AddChild(Button("Баны", () => _console.ExecuteCommand($"banlist \"{target}\"")));
            row.AddChild(Button("Бан", () => _console.ExecuteCommand($"banpanel \"{target}\"")));
        }
        if (_admin.HasFlag(AdminFlags.ViewNotes))
            row.AddChild(Button("Заметки", () => _console.ExecuteCommand($"adminnotes \"{target}\"")));
        if (!_players.TryGetSessionById(new NetUserId(target), out var player))
            return;
        foreach (var (command, title) in new[] { ("kick", "Кик"), ("respawn", "Респавн") })
        {
            if (!_admin.CanCommand(command)) continue;
            var control = new Button { Text = title };
            control.OnPressed += _ =>
            {
                if (AdminUIHelpers.TryConfirm(control, _confirmations))
                    _console.ExecuteCommand($"{command} \"{CommandParsing.Escape(player.Name)}\"");
            };
            row.AddChild(control);
        }
    }

    private static string Clock(long time) => DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime().ToString("HH:mm:ss");
    private static string Status(AhelpStatus status) => status switch
    {
        AhelpStatus.New => "Новый",
        AhelpStatus.Claimed => "В работе",
        AhelpStatus.Closing => "Завершается",
        _ => "Завершён",
    };

    private void OnNotice(AhelpNotice notice)
    {
        if (notice.Remind)
            return;
        _refresh = true;
        if (notice.Result.Length != 0)
            _status.Text = notice.Result;
    }

    private void OnState(AhelpSnapshot state)
    {
        var changedRound = _round.Length > 0 && state.Round.Length > 0 && state.Round != _round;
        if (state.Round.Length > 0) _round = state.Round;
        if (changedRound)
        {
            ResetThread();
            _ticket = _target = _operation = default;
            _uploads.Clear();
            _pending.Clear();
            _pinned.Clear();
            _tickets = [];
            _retryRequest = null;
            _retryBytes = null;
            _paste = null;
            _draft.Clear();
            _navigationState = null;
            _nextRead = TimeSpan.Zero;
            _refresh = true;
        }
        var read = _readId != Guid.Empty && state.Operation == _readId;
        _pending.TryGetValue(state.Operation, out var pending);
        if (!changedRound && state.Operation != Guid.Empty && pending == null && state.Operation != _operation && !read)
            return;
        if (read)
        {
            _reading = false;
            _readId = default;
        }
        if (state.Error.Length != 0)
        {
            _status.Text = Error(state.Error);
            if (pending != null)
            {
                pending.Error = state.Error;
                _uploads.Remove(state.Operation);
            }
            if (state.Operation == _operation)
            {
                _operation = default;
            }
            Render();
            if (_ticket != Guid.Empty)
                _refresh = true;
            return;
        }
        if (!read && state.Operation != Guid.Empty)
        {
            _pending.Remove(state.Operation);
            if (_operation == state.Operation)
            {
                _operation = default;
                _retryRequest = null;
                _retryBytes = null;
            }
            _status.Text = "";
        }
        _tickets = state.Tickets;
        if (pending != null && (pending.Request.Player != _target ||
            pending.Request.Ticket != Guid.Empty && pending.Request.Ticket != _ticket))
        {
            _refresh = true;
            Render();
            return;
        }
        if (state.Selected != null || !_older)
        {
            var selectedId = state.Selected?.Id ?? Guid.Empty;
            if (selectedId != _ticket)
            {
                StopTyping();
                _typingLabel.Text = "";
                _lines.Clear();
                _before = 0;
                ResetDownload();
                _requestedFile = null;
                ClearThumbnails();
                JumpToLatest();
            }
            _selected = state.Selected;
            _ticket = selectedId;
            if (_selected?.Status >= AhelpStatus.Closing)
            {
                StopTyping();
                _typingLabel.Text = "";
            }
            if (state.Selected != null && _isAdmin)
                _target = state.Selected.Player;
            if (_older || _lines.Count <= AhelpLimits.Page)
                _before = state.Before;
        }
        _older = false;
        foreach (var line in state.Lines)
        {
            if (_lines.TryGetValue(line.Id, out var previous) && previous.Mime != line.Mime)
                _thumbnailUnavailable.Remove(line.Id);
            _lines[line.Id] = line;
        }
        Render();
    }

    private static string Error(string error) => error switch
    {
        "rate_limited" => "Слишком много запросов.",
        "already_claimed" => "Тикет уже взят.",
        "assigned_to_other" => "Тикет уже закреплён за другим администратором.",
        "not_claimed" => "Тикет уже изменился.",
        "forbidden" => "Действие недоступно.",
        "reason_required" => "Укажите причину.",
        "invalid_image" => "Изображение повреждено или превышает лимит.",
        "closed" => "Тикет завершается.",
        "round_changed" => "Раунд сменился. Старые обращения и отправки очищены.",
        _ => "Не удалось выполнить действие. Обновите панель или повторите отправку.",
    };

    private void Older()
    {
        if (_before == 0 || _reading || _operation != Guid.Empty) return;
        _older = _reading = true;
        _readId = Guid.NewGuid();
        _deadline = _timing.RealTime + TimeSpan.FromSeconds(5);
        _network.SendTicket(new AhelpRequest(AhelpAction.Read, _ticket, _target, _readId, before: _before, round: _round));
    }

    private void OnTyping(AhelpTypingState state)
    {
        if (state.Round != _round || state.Ticket != _ticket) return;
        _typingLabel.Text = state.Names.Length == 0 ? "" : string.Join(", ", state.Names) +
            (state.Names.Length == 1 ? " печатает…" : " печатают…");
        _typingExpires = _timing.RealTime + TimeSpan.FromSeconds(9);
    }

    private void TypingInput(bool active)
    {
        if (!active) { StopTyping(); return; }
        _typingInput = true;
        _lastTypingInput = _timing.RealTime;
        TickTyping();
    }

    private void TickTyping()
    {
        if (_typingExpires != TimeSpan.Zero && _timing.RealTime >= _typingExpires)
        {
            _typingLabel.Text = "";
            _typingExpires = TimeSpan.Zero;
        }
        if (!_typingInput || !CanWrite || _ticket == Guid.Empty ||
            _timing.RealTime - _lastTypingInput > TimeSpan.FromSeconds(4) ||
            _native.Visible && !_draft.HasKeyboardFocus())
        {
            StopTyping();
            return;
        }
        if (_timing.RealTime < _nextTyping) return;
        _typingTicket = _ticket;
        _typingStaff = _isAdmin && _staff.Pressed;
        _network.SendTicketTyping(new AhelpTypingRequest(_ticket, true, _typingStaff, _round));
        _nextTyping = _timing.RealTime + TimeSpan.FromSeconds(2);
    }

    private void StopTyping()
    {
        if (_typingTicket != Guid.Empty)
            _network.SendTicketTyping(new AhelpTypingRequest(_typingTicket, false, _typingStaff, _round));
        _typingTicket = default;
        _typingInput = false;
        _nextTyping = TimeSpan.Zero;
    }

    private void SendDraft() => Mutate(AhelpAction.Send, _draft.Text);

    private void Mutate(AhelpAction action, string text = "", string file = "", byte[]? data = null)
    {
        if (action != AhelpAction.Send && _operation != Guid.Empty || text.Length > AhelpLimits.Text ||
            action == AhelpAction.Send && (!CanWrite || (data == null && string.IsNullOrWhiteSpace(text))))
            return;
        if (data != null && (_uploads.Count >= 3 || _pending.Values.Count(pending => pending.Data != null) >= 3))
        {
            _status.Text = "Дождитесь отправки одного из вложений.";
            return;
        }
        _readId = default;
        _reading = _older = false;
        var same = _retryRequest is { } prior && prior.Action == action && prior.Ticket == _ticket &&
            prior.Player == _target && prior.Text == text && prior.File == file && ReferenceEquals(data, _retryBytes);
        var id = same ? _retryRequest!.Operation : Guid.NewGuid();
        var request = new AhelpRequest(action, _ticket, _target, id, text, _isAdmin && _staff.Pressed,
            !_isAdmin || _sound.Pressed, file, data?.Length ?? 0, round: _round);
        StopTyping();
        if (action != AhelpAction.Send)
        {
            _retryRequest = request;
            _retryBytes = data;
        }
        else
        {
            _sent++;
            if (data == null) _draft.Clear();
        }
        Start(request, data);
    }

    private void Start(AhelpRequest request, byte[]? data)
    {
        if (request.Action != AhelpAction.Send) _operation = request.Operation;
        _pending[request.Operation] = new Pending(request, data, _timing.RealTime + TimeSpan.FromSeconds(12));
        if (data != null) _uploads[request.Operation] = new Upload(request, data) { Tick = _timing.CurTick };
        _network.SendTicket(request);
        Render();
    }

    private async void ChooseFile()
    {
        if (!CanWrite)
            return;
        (Guid Ticket, Guid Player, string Round) target = (_ticket, _target, _round);
        try
        {
            var (stream, name) = await _files.GetFileAndName(new FileDialogFilters(new FileDialogFilters.Group(
                "png", "jpg", "jpeg", "gif", "webp", "apng", "bmp", "tif", "tiff", "avif", "heic", "heif", "svg", "ico",
                "jxl", "psd", "tga", "qoi", "mp4", "webm", "m4a", "mp3", "wav", "ogg", "flac")), FileAccess.Read);
            if (stream == null) return;
            await using var input = stream;
            using var memory = new MemoryStream();
            var buffer = new byte[AhelpLimits.Chunk];
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + count > AhelpLimits.Image) throw new InvalidDataException();
                memory.Write(buffer, 0, count);
            }
            if (!_disposed && target.Round == _round && target.Player == _target && (target.Ticket == _ticket || target.Ticket == Guid.Empty) && memory.Length > 0)
            {
                name ??= "image";
                var start = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\')) + 1;
                Mutate(AhelpAction.Send, file: name[start..], data: memory.ToArray());
            }
        }
        catch (Exception) { if (!_disposed) _status.Text = Error("invalid_image"); }
    }

    private void OnWebAction(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var args, 65536) || args == null)
            return;
        switch (action)
        {
            case "typing" when args.String("round") == _round:
                TypingInput(args.String("active") == "true");
                break;
            case "send" when args.String("round") == _round: Mutate(AhelpAction.Send, args.String("text") ?? ""); break;
            case "file" when args.String("round") == _round: ChooseFile(); break;
            case "older": Older(); break;
            case "retry" when _operation == Guid.Empty && _retryRequest is { } request &&
                              request.Ticket == _ticket && request.Player == _target:
                Start(request, _retryBytes);
                break;
            case "retry-send" when Guid.TryParse(args.String("id"), out var retryId) && _pending.TryGetValue(retryId, out var pending):
                Start(pending.Request, pending.Data);
                break;
            case "view" when long.TryParse(args.String("id"), out var view): RequestFile(view, false); break;
            case "save" when long.TryParse(args.String("id"), out var save): RequestFile(save, true); break;
            case "paste-start" when CanWrite && args.String("round") == _round:
                _paste = new StringBuilder();
                _pasteTarget = (_ticket, _target, _round);
                _pasteFile = args.String("name") ?? "image";
                break;
            case "paste-chunk" when _paste != null:
                var text = args.String("data") ?? "";
                if (_paste.Length + text.Length <= (AhelpLimits.Image + 2) / 3 * 4)
                    _paste.Append(text);
                else
                    _paste = null;
                break;
            case "paste-end" when _paste != null:
                try
                {
                    if (_pasteTarget.Round == _round && _pasteTarget.Player == _target && (_pasteTarget.Ticket == _ticket || _pasteTarget.Ticket == Guid.Empty))
                        Mutate(AhelpAction.Send, file: _pasteFile, data: Convert.FromBase64String(_paste.ToString()));
                }
                catch (Exception) { _status.Text = Error("invalid_image"); }
                finally { _paste = null; }
                break;
        }
    }

    private void RequestFile(long id, bool save)
    {
        if (!_lines.TryGetValue(id, out var line) || line.File == null || line.Mime is AhelpLimits.Loading or AhelpLimits.Failed)
            return;
        _requestedFile = (id, save);
    }

    private void StartDownload(long id, bool thumbnail)
    {
        ResetDownload();
        _downloadId = id;
        _downloadingThumbnail = thumbnail;
        _downloadDeadline = _timing.RealTime + TimeSpan.FromSeconds(thumbnail ? 6 : 90);
        if (!thumbnail)
            _status.Text = "Загрузка изображения…";
        _network.SendTicket(new AhelpRequest(thumbnail ? AhelpAction.Preview : AhelpAction.Attachment, _ticket, attachment: id));
    }

    private void ResetDownload()
    {
        _downloadId = 0;
        _download = null;
        _downloadChunks = null;
        _received = 0;
    }

    private void OnDownload(AhelpDownload chunk)
    {
        if (chunk.Attachment != _downloadId || chunk.Preview != _downloadingThumbnail)
            return;
        if (chunk.Preview && chunk.Size == 0)
        {
            _thumbnailUnavailable.Add(chunk.Attachment);
            ResetDownload();
            return;
        }
        if (chunk.Size <= 0 || chunk.Size > (chunk.Preview ? AhelpLimits.Preview : AhelpLimits.Image) ||
            chunk.Offset < 0 || chunk.Offset >= chunk.Size || chunk.Offset % AhelpLimits.Chunk != 0 ||
            chunk.Bytes.Length != Math.Min(AhelpLimits.Chunk, chunk.Size - chunk.Offset))
            return;
        _download ??= new byte[chunk.Size];
        _downloadChunks ??= new bool[(chunk.Size + AhelpLimits.Chunk - 1) / AhelpLimits.Chunk];
        if (_download.Length != chunk.Size) return;
        var index = chunk.Offset / AhelpLimits.Chunk;
        if (_downloadChunks[index]) return;
        _downloadChunks[index] = true;
        chunk.Bytes.CopyTo(_download, chunk.Offset);
        if (++_received != _downloadChunks.Length) return;
        var bytes = _download;
        ResetDownload();
        if (chunk.Preview)
        {
            if (AhelpThumbnailData.Parse(bytes) is not { } thumbnail)
            {
                _thumbnailUnavailable.Add(chunk.Attachment);
                return;
            }
            if (_thumbnails.Count >= 24)
            {
                var oldest = _thumbnails.Keys.First();
                _thumbnails.Remove(oldest);
                if (_thumbnailTextures.Remove(oldest, out var texture))
                {
                    _nativeLines.DisposeAllChildren();
                    texture.Dispose();
                }
            }
            _thumbnails[chunk.Attachment] = thumbnail;
            if (_native.Visible) RenderNativeHistory();
            else RenderWebHistory();
            return;
        }
        _status.Text = "";
        if (!_lines.TryGetValue(chunk.Attachment, out var line)) return;
        var playable = line.Mime?.StartsWith("video/", StringComparison.Ordinal) == true || line.Mime?.StartsWith("audio/", StringComparison.Ordinal) == true;
        if (_save || _native.Visible || !playable && line.Mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif"))
        {
            Save(line.File ?? "image", bytes);
            return;
        }
        _previewId = chunk.Attachment;
        _preview = $"data:{line.Mime};base64,{Convert.ToBase64String(bytes)}";
        RenderWebHistory();
    }

    private void TickDownloads()
    {
        if (_downloadId != 0 && _timing.RealTime >= _downloadDeadline)
        {
            if (_downloadingThumbnail) _thumbnailUnavailable.Add(_downloadId);
            else _status.Text = Error("unavailable");
            ResetDownload();
        }
        if (_downloadId != 0)
            return;
        if (_requestedFile is { } request)
        {
            _requestedFile = null;
            _save = request.Save;
            StartDownload(request.Id, thumbnail: false);
            return;
        }
        if (_timing.RealTime < _nextThumbnail)
            return;
        var next = _lines.Values.Reverse().Where(line => line.File != null && line.Mime?.StartsWith("image/", StringComparison.Ordinal) == true).Take(12)
            .FirstOrDefault(line => !_thumbnails.ContainsKey(line.Id) && !_thumbnailUnavailable.Contains(line.Id));
        if (next == null)
            return;
        _nextThumbnail = _timing.RealTime + TimeSpan.FromMilliseconds(300);
        StartDownload(next.Id, thumbnail: true);
    }

    private async void Save(string name, byte[] bytes)
    {
        try
        {
            var split = name.LastIndexOf('.');
            var extension = split >= 0 ? name[(split + 1)..] : "";
            var output = await _files.SaveFile(extension.Length > 0
                ? new FileDialogFilters(new FileDialogFilters.Group(extension)) : null);
            if (output == null) return;
            await using var file = output.Value.fileStream;
            await file.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception) { if (!_disposed) _status.Text = "Не удалось сохранить изображение."; }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Tick();
    }

    private void Tick()
    {
        if (_disposed) return;
        TickTyping();
        if (_isAdmin && (_selected?.Player ?? _target) is var navigation && navigation != Guid.Empty && _timing.RealTime >= _nextNavigation)
        {
            _nextNavigation = _timing.RealTime + TimeSpan.FromSeconds(1);
            _network.SendTeleport(new AhelpTeleportRequest(navigation));
        }
        foreach (var (id, upload) in _uploads.ToArray())
        {
            if (_timing.CurTick <= upload.Tick) continue;
            upload.Tick = _timing.CurTick;
            var end = Math.Min(upload.Offset + AhelpLimits.Chunk, upload.Data.Length);
            _network.SendTicketChunk(new AhelpUpload(upload.Request.Operation, upload.Offset, upload.Data[upload.Offset..end]));
            upload.Offset = end;
            if (end == upload.Data.Length) _uploads.Remove(id);
        }
        var expired = false;
        foreach (var pending in _pending.Values)
        {
            if (pending.Error.Length != 0 || _timing.RealTime < pending.Deadline) continue;
            pending.Error = "unavailable";
            if (_operation == pending.Request.Operation) _operation = default;
            expired = true;
        }
        if (expired) Render();
        if (_reading && _timing.RealTime >= _deadline)
        {
            _reading = _older = false;
            _readId = default;
            _status.Text = Error("unavailable");
            Render();
        }
        TickDownloads();
        if (!_refresh || _reading || _timing.RealTime < _nextRead)
        {
            return;
        }
        _refresh = false;
        _reading = true;
        _readId = Guid.NewGuid();
        _nextRead = _timing.RealTime + TimeSpan.FromMilliseconds(300);
        _deadline = _timing.RealTime + TimeSpan.FromSeconds(5);
        _network.SendTicket(new AhelpRequest(AhelpAction.Read, _ticket, _target, _readId, round: _round));
    }

    private sealed class DetachedBody(Action tick) : Control
    {
        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);
            tick();
        }
    }

    private void PopOut()
    {
        if (_desktop != null) return;
        var clyde = IoCManager.Resolve<IClyde>();
        _desktop = clyde.CreateWindow(new WindowCreateParameters
        {
            Title = "Ahelp", Width = 1150, Height = 600, Monitor = clyde.EnumerateMonitors().First()
        });
        _desktop.DisposeOnClose = true;
        _desktop.RequestClosed += _ => Dispose();
        _desktopRoot = IoCManager.Resolve<IUserInterfaceManager>().CreateWindowRoot(_desktop);
        var detached = new DetachedBody(Tick);
        _body.Orphan();
        detached.AddChild(_body);
        _desktopRoot.AddChild(detached);
        Close();
        Render();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            StopTyping();
            _disposed = true;
            _input.FirstChanceOnKeyEvent -= OnInput;
            ClearThumbnails();
            _network.TicketSnapshot -= OnState;
            _network.TicketNotice -= OnNotice;
            _network.TicketDownload -= OnDownload;
            _network.TicketTeleportState -= OnTeleportState;
            _network.TicketTyping -= OnTyping;
            _network.TicketPlayerAction -= OnPlayerAction;
            _desktopRoot?.Dispose();
            _desktop?.Dispose();
            TicketClosed?.Invoke();
        }
        base.Dispose(disposing);
    }
}
