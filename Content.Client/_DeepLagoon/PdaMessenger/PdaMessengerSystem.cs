using System.IO;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.PDA;
using Content.Client.Resources;
using Content.Client.Stylesheets;
using Content.Shared._DeepLagoon.PdaMessenger;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using static Robust.Client.UserInterface.StylesheetHelpers;

namespace Content.Client._DeepLagoon.PdaMessenger;

public sealed partial class PdaMessengerSystem : EntitySystem
{
    [Dependency] private IFileDialogManager _dialogs = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private INetManager _network = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IClipboardManager _systemClipboard = default!;

    private sealed class OutgoingUpload(byte[] bytes, GameTick tick)
    {
        public readonly byte[] Bytes = bytes;
        public int Offset;
        public GameTick Tick = tick;
    }

    private PdaMenu? _menu;
    private BoxContainer? _native;
    private LineEdit? _nativeInput;
    private LineEdit? _nativeNumber;
    private NetEntity _pda;
    private PdaMessengerSnapshot? _state;
    private byte[]? _attachment;
    private string? _attachmentMime;
    private OutgoingUpload? _upload;
    private string? _clipboard;
    private byte[]? _receiving;
    private int _imageOffset;
    private int _imageId;
    private byte[]? _image;
    private OwnedTexture? _texture;
    private string _localStatus = "";
    private int _sentRevision;
    private int _contactRevision;
    private TimeSpan _nextOpenRetry;

    public override void Initialize()
    {
        UpdatesOutsidePrediction = true;
        SubscribeNetworkEvent<PdaMessengerSnapshot>(OnState);
        SubscribeNetworkEvent<PdaMessengerImageChunk>(OnImage);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(_ => _menu?.ToHomeScreen());
    }

    public override void Shutdown()
    {
        if (_menu is { } menu)
            Close(menu);
        base.Shutdown();
    }

    public void Open(EntityUid pda, PdaMenu menu)
    {
        if (_menu == menu)
            return;
        _menu?.ToHomeScreen();
        _menu = menu;
        _pda = GetNetEntity(pda);
        var nativeFont = new StackedFont(_resources.NotoStack(size: 12), _resources.GetFont("/Fonts/NotoEmoji.ttf", 12));
        var rules = _ui.Stylesheet?.Rules ?? Array.Empty<StyleRule>();
        _native = menu.MessengerView;
        _native.Stylesheet = new Stylesheet(rules.Concat(
            [Element<RichTextLabel>().Prop("font", nativeFont), Element<LineEdit>().Prop("font", nativeFont)]).ToArray());
        _nextOpenRetry = _timing.RealTime + TimeSpan.FromSeconds(1);
        Publish();
        Request(PdaMessengerAction.Open);
    }

    public void Close(PdaMenu menu)
    {
        if (_menu != menu)
            return;

        if (_network.IsConnected)
            Request(PdaMessengerAction.Close);
        _native?.RemoveAllChildren();
        ClearImage();
        _menu = null;
        _native = null;
        _nativeInput = null;
        _nativeNumber = null;
        _state = null;
        _attachment = null;
        _attachmentMime = null;
        _upload = null;
        _clipboard = null;
        _localStatus = "";
        menu.SetMessengerData(null);
    }

    public void ShowNative(PdaMenu menu)
    {
        if (_menu == menu)
            DrawNative();
    }

    private void OnState(PdaMessengerSnapshot state)
    {
        if (_menu == null || _pda != state.Pda)
            return;

        if (_state?.Peer != state.Peer)
        {
            _attachment = null;
            _attachmentMime = null;
            _clipboard = null;
            _nativeInput?.Clear();
            ClearImage();
        }

        _state = state;
        _localStatus = "";
        if (state.Sent)
        {
            _attachment = null;
            _attachmentMime = null;
            _nativeInput?.Clear();
            _sentRevision++;
        }
        if (state.ContactAdded)
        {
            _nativeNumber?.Clear();
            _contactRevision++;
        }
        Publish();
    }

    private void ClearImage()
    {
        _receiving = null;
        _image = null;
        _imageId = 0;
        _texture?.Dispose();
        _texture = null;
    }

    private void Request(PdaMessengerAction action, int target = 0, string text = "", int length = 0) =>
        RaiseNetworkEvent(new PdaMessengerRequest(_pda, action, target, text, length));

    private void Send(string text)
    {
        if (_upload != null || _state is not { Peer: > 0 } state ||
            !state.Contacts.Any(contact => contact.Id == state.Peer && contact.Saved && contact.Available) ||
            text.Length > PdaMessengerLimits.TextLength)
            return;

        var image = state.Attachments ? _attachment : null;
        if (string.IsNullOrWhiteSpace(text) && image == null)
            return;

        Request(PdaMessengerAction.Send, state.Peer, text, image?.Length ?? 0);
        if (image != null)
            _upload = new OutgoingUpload(image, _timing.CurTick);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_menu != null && _state == null && _network.IsConnected && _timing.RealTime >= _nextOpenRetry)
        {
            _nextOpenRetry = _timing.RealTime + TimeSpan.FromSeconds(3);
            Request(PdaMessengerAction.Open);
        }

        if (_upload is not { } upload || _timing.CurTick <= upload.Tick)
            return;

        if (!_network.IsConnected)
        {
            _upload = null;
            return;
        }

        // Entity events from one tick can be reordered by the server's dispatch queue.
        upload.Tick = _timing.CurTick;
        var end = Math.Min(upload.Bytes.Length, upload.Offset + PdaMessengerLimits.ChunkBytes);
        RaiseNetworkEvent(new PdaMessengerUploadChunk(_pda, upload.Offset, upload.Bytes[upload.Offset..end]));
        upload.Offset = end;
        if (end == upload.Bytes.Length)
            _upload = null;
    }

    public void HandleAction(PdaMenu menu, string action, string payload)
    {
        if (_menu != menu || !TguiActionData.TryParse(payload, out var args) || args == null)
            return;

        switch (action)
        {
            case "refresh": Request(_state == null ? PdaMessengerAction.Open : PdaMessengerAction.Refresh); break;
            case "select" when args.TryInt("id", out var peer): Request(PdaMessengerAction.Select, peer); break;
            case "add": AddContact(args.String("number") ?? ""); break;
            case "remove" when args.TryInt("id", out var contact): Request(PdaMessengerAction.RemoveContact, contact); break;
            case "copy-number" when _state is { } state: _systemClipboard.SetText(state.Number.ToString()); break;
            case "image" when args.TryInt("id", out var id): Request(PdaMessengerAction.ReadImage, id); break;
            case "send": Send(args.String("text") ?? ""); break;
            case "file": OpenFile(); break;
            case "detach":
                _attachment = null;
                _attachmentMime = null;
                Publish();
                break;
            case "clipboard-start" when _state?.Attachments == true &&
                args.TryInt("peer", out var clipboardPeer) && clipboardPeer == _state.Peer:
                _clipboard = "";
                break;
            case "clipboard-chunk" when _clipboard != null:
                var chunk = args.String("data") ?? "";
                if (_clipboard.Length + chunk.Length <= (PdaMessengerLimits.ImageBytes + 2) / 3 * 4)
                    _clipboard += chunk;
                else
                    _clipboard = null;
                break;
            case "clipboard-end" when _clipboard != null:
                try { StageImage(Convert.FromBase64String(_clipboard)); }
                catch (Exception) { AttachmentError(); }
                _clipboard = null;
                break;
        }
    }

    private async void OpenFile()
    {
        if (_state?.Attachments != true || _menu is not { } menu)
            return;

        var peer = _state.Peer;
        try
        {
            await using var file = await _dialogs.OpenFile(new FileDialogFilters(
                new FileDialogFilters.Group("png", "jpg", "jpeg")), FileAccess.Read);
            if (file == null || _menu != menu || _state?.Peer != peer)
                return;

            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = await file.ReadAsync(block)) > 0)
            {
                if (buffer.Length + count > PdaMessengerLimits.ImageBytes)
                {
                    AttachmentError();
                    return;
                }
                buffer.Write(block, 0, count);
            }
            if (_menu == menu && _state?.Peer == peer)
                StageImage(buffer.ToArray());
        }
        catch (Exception)
        {
            if (_menu == menu)
                AttachmentError();
        }
    }

    private void StageImage(byte[] bytes)
    {
        var mime = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47
            ? "image/png" : bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff
                ? "image/jpeg" : null;
        if (_state?.Attachments != true || mime == null || bytes.Length > PdaMessengerLimits.ImageBytes)
        {
            AttachmentError();
            return;
        }

        _attachment = bytes;
        _attachmentMime = mime;
        _localStatus = "";
        Publish();
    }

    private void AttachmentError()
    {
        _localStatus = Loc.GetString("dl-messenger-file-error");
        Publish();
    }

    private void OnImage(PdaMessengerImageChunk chunk)
    {
        if (_menu == null || chunk.Pda != _pda || _state == null ||
            !_state.Messages.Any(item => item.Id == chunk.Message && item.HasImage) ||
            chunk.Total is < 1 or > PdaMessengerLimits.ImageBytes)
            return;

        if (chunk.Offset == 0)
        {
            ClearImage();
            _imageId = chunk.Message;
            _receiving = new byte[chunk.Total];
            _imageOffset = 0;
        }

        if (_receiving == null || _imageId != chunk.Message || chunk.Offset != _imageOffset ||
            chunk.Bytes.Length > _receiving.Length - _imageOffset)
            return;

        chunk.Bytes.CopyTo(_receiving, _imageOffset);
        _imageOffset += chunk.Bytes.Length;
        if (_imageOffset != _receiving.Length)
            return;

        _image = _receiving;
        _receiving = null;
        Publish();
    }

    private string Label(string key) => Loc.GetString("dl-messenger-" + key);

    private void Publish()
    {
        if (_menu == null)
            return;

        var labels = new TguiData();
        foreach (var key in new[] { "waiting", "refresh", "contacts", "select", "offline-label", "write",
                     "send", "file", "image", "remove", "paste", "you", "empty", "file-error", "your-number", "number-input",
                     "add-contact", "remove-contact", "empty-contacts", "incoming", "save-to-reply", "number-help",
                     "your-temporary-number" })
            labels.String(key, Label(key));

        var data = new TguiData().Object("labels", labels).Number("peer", _state?.Peer ?? 0)
            .Bool("ready", _state != null).Bool("attachments", _state?.Attachments == true)
            .Number("sentRevision", _sentRevision).Number("contactRevision", _contactRevision)
            .Number("number", _state?.Number ?? 0)
            .Bool("temporary", _state?.Number >= PdaMessengerLimits.FirstTemporaryNumber)
            .String("status", _localStatus.Length > 0 ? _localStatus : _state?.Status)
            .Number("imageId", _imageId).String("image", _image == null ? "" : DataUrl(_image, "image/png"))
            .String("attachment", _attachment == null ? "" : DataUrl(_attachment, _attachmentMime!))
            .Array("contacts", (_state?.Contacts ?? []).Select(contact => new TguiData().Number("id", contact.Id)
                .Bool("saved", contact.Saved).Bool("available", contact.Available).Bool("unread", contact.Unread)))
            .Array("messages", (_state?.Messages ?? []).Select(message => new TguiData().Number("id", message.Id)
                .Bool("outgoing", message.Outgoing).String("text", message.Text).Bool("hasImage", message.HasImage)));
        _menu.SetMessengerData(data);
        if (!_menu.OrbitalActive)
            DrawNative();
    }

    private static string DataUrl(byte[] bytes, string mime) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    private void AddContact(string text)
    {
        if (!PdaMessengerLimits.TryParseNumber(text, out var number))
        {
            _localStatus = Label("invalid-number");
            Publish();
            return;
        }

        Request(PdaMessengerAction.AddContact, text: number.ToString());
    }

    private void DrawNative()
    {
        if (_native == null)
            return;

        var draft = _nativeInput?.Text ?? "";
        var numberDraft = _nativeNumber?.Text ?? "";
        var messageFocused = _nativeInput?.HasKeyboardFocus() == true;
        var numberFocused = _nativeNumber?.HasKeyboardFocus() == true;
        _nativeInput = null;
        _nativeNumber = null;
        _native.RemoveAllChildren();
        var header = new BoxContainer { SeparationOverride = 6 };
        _native.AddChild(header);
        header.AddChild(new Control { HorizontalExpand = true });
        header.AddChild(Button("refresh", () => Request(_state == null ? PdaMessengerAction.Open : PdaMessengerAction.Refresh)));
        if (_state == null)
        {
            _native.AddChild(Wrapped(Label("waiting")));
            return;
        }

        var own = _state.Number.ToString();
        var ownLabel = _state.Number >= PdaMessengerLimits.FirstTemporaryNumber ? "your-temporary-number" : "your-number";
        var ownNumber = new Button { Text = Label(ownLabel) + " · " + own };
        ownNumber.OnPressed += _ => _systemClipboard.SetText(own);
        header.AddChild(ownNumber);

        var addRow = new BoxContainer { SeparationOverride = 6 };
        var numberInput = new LineEdit
        {
            Text = numberDraft,
            PlaceHolder = Label("number-input"),
            HorizontalExpand = true,
            IsValid = text => text.Length <= PdaMessengerLimits.NumberInputLength,
        };
        _nativeNumber = numberInput;
        numberInput.OnTextEntered += _ => AddContact(numberInput.Text);
        addRow.AddChild(numberInput);
        addRow.AddChild(Button("add-contact", () => AddContact(numberInput.Text)));
        _native.AddChild(addRow);
        _native.AddChild(Wrapped(Label("number-help")));
        if (numberFocused && numberInput.VisibleInTree)
            numberInput.GrabKeyboardFocus();

        var columns = new BoxContainer { SeparationOverride = 8, VerticalExpand = true };
        _native.AddChild(columns);
        var contacts = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = 165 };
        var history = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        foreach (var list in new[] { contacts, history })
        {
            var scroll = new ScrollContainer { HorizontalExpand = list == history, VerticalExpand = true };
            scroll.AddChild(list);
            columns.AddChild(scroll);
        }

        contacts.AddChild(new Label { Text = Label("contacts") });
        var saved = _state.Contacts.Where(contact => contact.Saved).ToArray();
        if (saved.Length == 0)
            contacts.AddChild(Wrapped(Label("empty-contacts")));
        foreach (var contact in saved)
            contacts.AddChild(ContactButton(contact));
        var incoming = _state.Contacts.Where(contact => !contact.Saved).ToArray();
        if (incoming.Length != 0)
        {
            contacts.AddChild(new Label { Text = Label("incoming") });
            foreach (var contact in incoming)
                contacts.AddChild(ContactButton(contact));
        }

        var peer = _state.Contacts.FirstOrDefault(contact => contact.Id == _state.Peer);
        if (peer == null)
            history.AddChild(Wrapped(Label("select")));
        else if (_state.Messages.Length == 0)
            history.AddChild(Wrapped(Label("empty")));
        foreach (var message in _state.Messages)
        {
            if (message.Text.Length > 0)
            {
                history.AddChild(Wrapped((message.Outgoing ? Label("you") + " " : "") + message.Text));
            }

            if (message.HasImage)
                history.AddChild(Button("image", () => Request(PdaMessengerAction.ReadImage, message.Id)));
            if (message.Id != _imageId || _image == null)
                continue;

            try
            {
                using var stream = new MemoryStream(_image);
                _texture ??= _clyde.LoadTextureFromPNGStream(stream, "PDA attachment");
                history.AddChild(new TextureRect { Texture = _texture, Stretch = TextureRect.StretchMode.KeepAspectCentered,
                    SetSize = new Vector2(240, 160) });
            }
            catch (Exception) { history.AddChild(new Label { Text = Label("file-error") }); }
        }

        var status = _localStatus.Length > 0 ? _localStatus : _state.Status;
        if (status.Length != 0)
            _native.AddChild(Wrapped(status));
        if (peer == null)
            return;

        if (!peer.Saved)
        {
            _native.AddChild(Wrapped(Label("save-to-reply")));
            _native.AddChild(Button("add-contact", () => AddContact(peer.Id.ToString())));
            return;
        }
        if (!peer.Available)
            _native.AddChild(Wrapped(Label("offline")));

        var composer = new BoxContainer { SeparationOverride = 4 };
        _native.AddChild(composer);
        var input = new LineEdit
        {
            Text = draft, PlaceHolder = Label("write"), HorizontalExpand = true, Editable = peer.Available,
            IsValid = text => text.Length <= PdaMessengerLimits.TextLength,
        };
        _nativeInput = input;
        input.OnTextEntered += _ => Send(input.Text);
        composer.AddChild(input);
        if (_state.Attachments)
        {
            var attachment = Button(_attachment == null ? "file" : "remove", () =>
            {
                if (_attachment == null) OpenFile();
                else { _attachment = null; _attachmentMime = null; Publish(); }
            });
            attachment.Disabled = _attachment == null && !peer.Available;
            composer.AddChild(attachment);
        }
        var send = Button("send", () => Send(input.Text));
        send.Disabled = !peer.Available;
        composer.AddChild(send);
        _native.AddChild(Button("remove-contact", () => Request(PdaMessengerAction.RemoveContact, peer.Id)));
        if (messageFocused && input.VisibleInTree && input.Editable)
            input.GrabKeyboardFocus();
    }

    private Button ContactButton(PdaMessengerContact contact)
    {
        var button = new Button
        {
            Text = (contact.Unread ? "● " : "") + contact.Id,
            ToolTip = contact.Available ? null : Label("offline-label"),
            ToggleMode = true,
            Pressed = _state?.Peer == contact.Id,
        };
        button.OnPressed += _ => Request(PdaMessengerAction.Select, contact.Id);
        return button;
    }

    private static RichTextLabel Wrapped(string text)
    {
        var label = new RichTextLabel();
        label.SetMessage(FormattedMessage.FromUnformatted(text));
        return label;
    }

    private Button Button(string key, Action action)
    {
        var button = new Button { Text = Label(key) };
        button.OnPressed += _ => action();
        return button;
    }
}
