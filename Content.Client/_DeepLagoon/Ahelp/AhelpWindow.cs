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
    private readonly BoxContainer _sidebar = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = 235, SeparationOverride = 5 };
    private readonly BoxContainer _rows = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 3 };
    private readonly BoxContainer _thread = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 5 };
    private readonly BoxContainer _toolbar = new() { SeparationOverride = 3 };
    private readonly BoxContainer _playerActions = new() { SeparationOverride = 3 };
    private readonly BoxContainer _native = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, VerticalExpand = true, Visible = false, SeparationOverride = 6 };
    private readonly BoxContainer _nativeLines = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly TicketHistoryScroll _nativeHistory = new() { VerticalExpand = true };
    private readonly VScrollBar _nativeScroll;
    private readonly Label _header = new() { FontColorOverride = Color.FromHex("#C0DBFF") };
    private readonly Label _status = new();
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
    private Upload? _upload;
    private StringBuilder? _paste;
    private (Guid Ticket, Guid Player) _pasteTarget;
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
    private IClydeWindow? _desktop;
    private WindowRoot? _desktopRoot;
    public event Action? TicketClosed;

    public AhelpWindow(BwoinkSystem network, Guid actor, bool admin)
    {
        _network = network;
        _actor = actor;
        _isAdmin = admin;
        Title = "Ahelp";
        MinSize = new Vector2(admin ? 780 : 510, 450);
        SetSize = new Vector2(admin ? 950 : 640, 600);
        ContentsContainer.AddChild(Panel("#181F29", _body, 8));
        if (admin)
        {
            _body.AddChild(Panel("#222B39", _sidebar, 6));
            var filters = new BoxContainer { SeparationOverride = 3 };
            foreach (var (label, index) in new[] { ("Новые", 0), ("Мои", 1), ("В работе", 2), ("Игроки", 3) })
                filters.AddChild(Button(label, () => { _filter = index; PopulateQueue(); }));
            _sidebar.AddChild(filters);
            _sidebar.AddChild(_search);
            _search.OnTextChanged += _ => PopulateQueue();
            _sidebar.AddChild(new ScrollContainer { VerticalExpand = true, Children = { _rows } });
        }
        _body.AddChild(_thread);
        _thread.AddChild(Panel("#2A384B", _header, 7));
        _thread.AddChild(_toolbar);
        _thread.AddChild(_playerActions);
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
        var timeline = Panel("#151C25", _nativeHistory, 5);
        timeline.AddChild(_nativeLatest);
        _native.AddChild(timeline);
        _nativeHistory.OnScrolled += UpdateLatestButton;
        _native.AddChild(Panel("#273346", new BoxContainer
        {
            SeparationOverride = 5,
            Children = { _draft, _nativeFile, _nativeSend }
        }, 5));
        if (admin)
            _thread.AddChild(new BoxContainer { Children = { _staff, _sound } });
        _staff.OnToggled += args =>
        {
            _sound.Disabled = args.Pressed;
            Render();
        };
        _draft.OnTextEntered += _ => SendDraft();
        _web.SetNativeFallback(() => { _native.Visible = true; _web.Visible = false; Render(); FocusPlayerComposer(); });
        _web.SetTguiRestore(() => { _native.Visible = false; _web.Visible = true; Render(); FocusPlayerComposer(); });
        _web.Web.Ready += FocusPlayerComposer;
        _web.OnAction += OnWebAction;
        _input.FirstChanceOnKeyEvent += OnInput;
        _network.TicketSnapshot += OnState;
        _network.TicketNotice += OnNotice;
        _network.TicketDownload += OnDownload;
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

    private bool CanWrite => _selected == null ? !_isAdmin || _target != Guid.Empty :
        _selected.Status < AhelpStatus.Closing &&
        (_isAdmin && _staff.Pressed || _selected.Player == _actor || _selected.Assignee == _actor);

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
        _nativeFile.Disabled = _nativeSend.Disabled = !CanWrite || _operation != Guid.Empty;
        var follow = _nativeScroll.IsAtEnd;
        _nativeLines.DisposeAllChildren();
        foreach (var line in _lines.Values)
        {
            var card = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 4 };
            card.AddChild(new Label
            {
                Text = $"{line.AuthorName}  ·  {Clock(line.At)}{(line.Staff ? "  ·  для администраторов" : "")}",
                FontColorOverride = Color.FromHex(line.Staff ? "#E7C38A" : "#BFD6F2"),
            });
            if (line.Text.Length > 0)
            {
                var label = new RichTextLabel();
                var message = new FormattedMessage();
                message.AddText(line.Text);
                label.SetMessage(message);
                card.AddChild(label);
            }
            if (line.File != null)
            {
                if (GetThumbnail(line.Id) is { } thumbnail)
                    card.AddChild(thumbnail.CreateControl());
                var save = Button("Сохранить · " + line.File, () => RequestFile(line.Id, true));
                save.HorizontalAlignment = HAlignment.Left;
                card.AddChild(save);
            }
            _nativeLines.AddChild(Panel(line.Staff ? "#3B3329" : line.Author == _actor ? "#26394D" : "#222D3D", card, 7));
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

    private void RenderWebHistory()
    {
        _web.SetIncrementalState("AhelpTicket", new TguiData().Bool("writable", CanWrite)
            .Bool("focusComposer", !_isAdmin).Bool("busy", _operation != Guid.Empty).Bool("older", _before > 0).Number("sent", _sent)
            .Bool("retry", _operation == Guid.Empty && _retryRequest is { } retry && retry.Ticket == _ticket && retry.Player == _target)
            .String("ticket", _ticket + "/" + _target).String("preview", _preview).Number("previewId", _previewId)
            .Array("thumbnails", _thumbnails.Select(entry => entry.Value.ToWeb(entry.Key)))
            .Array("lines", _lines.Values.Select(line => new TguiData().Number("id", line.Id).String("at", Clock(line.At))
                .String("author", line.AuthorName).String("text", line.Text).String("file", line.File).Bool("staff", line.Staff))));
    }

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
        if (_admin.CanCommand("follow") && player.AttachedEntity is { } entity)
        {
            var entities = IoCManager.Resolve<IEntityManager>();
            row.AddChild(Button("Следить", () => _console.ExecuteCommand($"follow \"{entities.GetNetEntity(entity)}\"")));
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
        var read = _readId != Guid.Empty && state.Operation == _readId;
        if (state.Operation != Guid.Empty && state.Operation != _operation && !read)
            return;
        if (read)
        {
            _reading = false;
            _readId = default;
        }
        if (state.Error.Length != 0)
        {
            _status.Text = Error(state.Error);
            if (state.Operation == _operation)
            {
                _operation = default;
                _upload = null;
                Render();
            }
            if (_ticket != Guid.Empty)
                _refresh = true;
            return;
        }
        if (!read && state.Operation != Guid.Empty)
        {
            if (_retryRequest?.Action == AhelpAction.Send && _retryBytes == null)
            {
                _sent++;
                _draft.Clear();
            }
            _operation = default;
            _retryRequest = null;
            _retryBytes = null;
            _upload = null;
            _status.Text = "";
        }
        _tickets = state.Tickets;
        if (state.Selected != null || !_older)
        {
            var selectedId = state.Selected?.Id ?? Guid.Empty;
            if (selectedId != _ticket)
            {
                _lines.Clear();
                _before = 0;
                ResetDownload();
                _requestedFile = null;
                ClearThumbnails();
                JumpToLatest();
            }
            _selected = state.Selected;
            _ticket = selectedId;
            if (state.Selected != null && _isAdmin)
                _target = state.Selected.Player;
            if (_older || _lines.Count <= AhelpLimits.Page)
                _before = state.Before;
        }
        _older = false;
        foreach (var line in state.Lines)
            _lines[line.Id] = line;
        Render();
    }

    private static string Error(string error) => error switch
    {
        "rate_limited" => "Слишком много запросов.",
        "already_claimed" => "Тикет уже взят.",
        "not_claimed" => "Тикет уже изменился.",
        "forbidden" => "Действие недоступно.",
        "reason_required" => "Укажите причину.",
        "invalid_image" => "Изображение повреждено или превышает лимит.",
        "closed" => "Тикет завершается.",
        _ => "Не удалось выполнить действие. Обновите панель или повторите отправку.",
    };

    private void Older()
    {
        if (_before == 0 || _reading || _operation != Guid.Empty) return;
        _older = _reading = true;
        _readId = Guid.NewGuid();
        _deadline = _timing.RealTime + TimeSpan.FromSeconds(5);
        _network.SendTicket(new AhelpRequest(AhelpAction.Read, _ticket, _target, _readId, before: _before));
    }

    private void SendDraft() => Mutate(AhelpAction.Send, _draft.Text);

    private void Mutate(AhelpAction action, string text = "", string file = "", byte[]? data = null)
    {
        if (_operation != Guid.Empty || _reading || text.Length > AhelpLimits.Text ||
            action == AhelpAction.Send && (!CanWrite || (data == null && string.IsNullOrWhiteSpace(text))))
            return;
        var same = _retryRequest is { } prior && prior.Action == action && prior.Ticket == _ticket &&
            prior.Player == _target && prior.Text == text && prior.File == file && ReferenceEquals(data, _retryBytes);
        var id = same ? _retryRequest!.Operation : Guid.NewGuid();
        var request = new AhelpRequest(action, _ticket, _target, id, text, _isAdmin && _staff.Pressed,
            !_isAdmin || _sound.Pressed, file, data?.Length ?? 0);
        _retryRequest = request;
        _retryBytes = data;
        Start(request, data);
    }

    private void Start(AhelpRequest request, byte[]? data)
    {
        _operation = request.Operation;
        _status.Text = data == null ? "Отправка…" : "Загрузка изображения…";
        _deadline = _timing.RealTime + TimeSpan.FromSeconds(data == null ? 12 : 110);
        if (data != null) _upload = new Upload(request, data) { Tick = _timing.CurTick };
        _network.SendTicket(request);
        Render();
    }

    private async void ChooseFile()
    {
        if (!CanWrite || _operation != Guid.Empty)
            return;
        var target = (_ticket, _target);
        try
        {
            var (stream, name) = await _files.GetFileAndName(new FileDialogFilters(new FileDialogFilters.Group(
                "png", "jpg", "jpeg", "gif", "webp", "apng", "bmp", "tif", "tiff", "avif", "heic", "heif", "svg", "ico",
                "jxl", "psd", "tga", "qoi")), FileAccess.Read);
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
            if (!_disposed && target == (_ticket, _target) && memory.Length > 0)
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
            case "send": Mutate(AhelpAction.Send, args.String("text") ?? ""); break;
            case "file": ChooseFile(); break;
            case "older": Older(); break;
            case "retry" when _operation == Guid.Empty && _retryRequest is { } request &&
                              request.Ticket == _ticket && request.Player == _target:
                Start(request, _retryBytes);
                break;
            case "view" when long.TryParse(args.String("id"), out var view): RequestFile(view, false); break;
            case "save" when long.TryParse(args.String("id"), out var save): RequestFile(save, true); break;
            case "paste-start" when CanWrite && _operation == Guid.Empty:
                _paste = new StringBuilder();
                _pasteTarget = (_ticket, _target);
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
                    if (_pasteTarget == (_ticket, _target))
                        Mutate(AhelpAction.Send, file: _pasteFile, data: Convert.FromBase64String(_paste.ToString()));
                }
                catch (Exception) { _status.Text = Error("invalid_image"); }
                finally { _paste = null; }
                break;
        }
    }

    private void RequestFile(long id, bool save)
    {
        if (!_lines.TryGetValue(id, out var line) || line.File == null)
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
        if (_save || _native.Visible || line.Mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif"))
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
        if (_downloadId != 0 || _operation != Guid.Empty || _reading)
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
        var next = _lines.Values.Reverse().Where(line => line.File != null).Take(12)
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
        if (_upload is { } upload && _timing.CurTick > upload.Tick)
        {
            upload.Tick = _timing.CurTick;
            var end = Math.Min(upload.Offset + AhelpLimits.Chunk, upload.Data.Length);
            _network.SendTicketChunk(new AhelpUpload(upload.Request.Operation, upload.Offset, upload.Data[upload.Offset..end]));
            upload.Offset = end;
            _status.Text = $"Загрузка изображения… {100L * end / upload.Data.Length}%";
            if (end == upload.Data.Length) _upload = null;
        }
        if ((_operation != Guid.Empty || _reading) && _timing.RealTime >= _deadline)
        {
            _operation = default;
            _upload = null;
            _reading = _older = false;
            _readId = default;
            _status.Text = Error("unavailable");
            Render();
        }
        if (!_refresh || _reading || _operation != Guid.Empty || _timing.RealTime < _nextRead)
        {
            TickDownloads();
            return;
        }
        _refresh = false;
        _reading = true;
        _readId = Guid.NewGuid();
        _nextRead = _timing.RealTime + TimeSpan.FromMilliseconds(300);
        _deadline = _timing.RealTime + TimeSpan.FromSeconds(5);
        _network.SendTicket(new AhelpRequest(AhelpAction.Read, _ticket, _target, _readId));
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
            Title = "Ahelp", Width = 950, Height = 600, Monitor = clyde.EnumerateMonitors().First()
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
            _disposed = true;
            _input.FirstChanceOnKeyEvent -= OnInput;
            ClearThumbnails();
            _network.TicketSnapshot -= OnState;
            _network.TicketNotice -= OnNotice;
            _network.TicketDownload -= OnDownload;
            _desktopRoot?.Dispose();
            _desktop?.Dispose();
            TicketClosed?.Invoke();
        }
        base.Dispose(disposing);
    }
}
