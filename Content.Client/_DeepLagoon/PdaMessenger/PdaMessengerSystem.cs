using System.IO;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.PdaMessenger;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.PdaMessenger;

public sealed class PdaMessengerSystem : EntitySystem
{
    [Dependency] private IFileDialogManager _dialogs = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private INetManager _network = default!;

    private FancyWindow? _window;
    private TguiPanel? _panel;
    private BoxContainer? _native;
    private LineEdit? _nativeInput;
    private NetEntity _pda;
    private PdaMessengerSnapshot? _state;
    private byte[]? _attachment;
    private string? _attachmentMime;
    private string? _clipboard;
    private byte[]? _receiving;
    private int _imageOffset;
    private int _imageId;
    private byte[]? _image;
    private OwnedTexture? _texture;
    private string _localStatus = "";
    private int _sentRevision;

    public override void Initialize()
    {
        SubscribeNetworkEvent<PdaMessengerSnapshot>(OnState);
        SubscribeNetworkEvent<PdaMessengerImageChunk>(OnImage);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(_ => _window?.Close());
    }

    public override void Shutdown()
    {
        _window?.Close();
        base.Shutdown();
    }

    public void Open(EntityUid pda)
    {
        _window?.Close();
        _pda = GetNetEntity(pda);
        var window = new FancyWindow
        {
            Title = Loc.GetString("dl-messenger-title"),
            SetSize = new Vector2(650, 465),
            MinSize = new Vector2(430, 320),
        };
        _window = window;
        _native = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 6,
            Visible = false,
        };
        var host = new CivilianConsoleHost(() =>
        {
            if (_native != null)
                _native.Visible = true;
            DrawNative();
        });
        var panel = host.Panel;
        _panel = panel;
        panel.SetTguiRestore(() =>
        {
            host.Visible = true;
            if (_native != null)
                _native.Visible = false;
        });
        panel.OnAction += OnAction;
        panel.OnClose += window.Close;
        window.ContentsContainer.AddChild(host);
        window.ContentsContainer.AddChild(_native);
        window.OnClose += () =>
        {
            if (_state != null && _network.IsConnected)
                Request(PdaMessengerAction.Close);
            ClearImage();
            _window = null;
            _panel = null;
            _native = null;
            _nativeInput = null;
            _state = null;
            _attachment = null;
            _attachmentMime = null;
            _clipboard = null;
            _localStatus = "";
            window.Dispose();
        };
        panel.BindStyle(window, "PdaMessenger", () => !panel.UsingNative, web =>
        {
            if (web)
                panel.SwitchToTgui();
            else
                panel.SwitchToNative();
        });
        Publish();
        if (!WindowInterfaceStyle.UseTgui("PdaMessenger"))
            panel.SwitchToNative();
        window.OpenCentered();
        Request(PdaMessengerAction.Open);
    }

    private void OnState(PdaMessengerSnapshot state)
    {
        if (_window == null || _pda != state.Pda)
            return;

        if (_state?.Peer != state.Peer)
        {
            _attachment = null;
            _attachmentMime = null;
            _clipboard = null;
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
        if (_state is not { Peer: > 0 } state || text.Length > PdaMessengerLimits.TextLength ||
            string.IsNullOrWhiteSpace(text) && _attachment == null)
            return;

        var image = state.Attachments ? _attachment : null;
        Request(PdaMessengerAction.Send, state.Peer, text, image?.Length ?? 0);
        if (image == null)
            return;

        for (var offset = 0; offset < image.Length; offset += PdaMessengerLimits.ChunkBytes)
        {
            var length = Math.Min(PdaMessengerLimits.ChunkBytes, image.Length - offset);
            RaiseNetworkEvent(new PdaMessengerUploadChunk(_pda, offset, image.AsSpan(offset, length).ToArray()));
        }
    }

    private void OnAction(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var args) || args == null)
            return;

        switch (action)
        {
            case "refresh": Request(_state == null ? PdaMessengerAction.Open : PdaMessengerAction.Refresh); break;
            case "select" when args.TryInt("id", out var peer): Request(PdaMessengerAction.Select, peer); break;
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
        if (_state?.Attachments != true || _window is not { } window)
            return;

        var peer = _state.Peer;
        try
        {
            await using var file = await _dialogs.OpenFile(new FileDialogFilters(
                new FileDialogFilters.Group("png", "jpg", "jpeg")), FileAccess.Read);
            if (file == null || _window != window || _state?.Peer != peer)
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
            if (_window == window && _state?.Peer == peer)
                StageImage(buffer.ToArray());
        }
        catch (Exception)
        {
            if (_window == window)
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
        if (_window == null || chunk.Pda != _pda || _state == null ||
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
        if (_panel == null)
            return;

        var labels = new TguiData();
        foreach (var key in new[] { "title", "waiting", "refresh", "contacts", "select", "offline-label", "write",
                     "send", "file", "image", "remove", "paste", "you", "empty", "file-error" })
            labels.String(key, Label(key));

        var data = new TguiData().Object("labels", labels).Number("peer", _state?.Peer ?? 0)
            .Bool("ready", _state != null).Bool("attachments", _state?.Attachments == true)
            .Number("sentRevision", _sentRevision)
            .String("status", _localStatus.Length > 0 ? _localStatus : _state?.Status)
            .Number("imageId", _imageId).String("image", _image == null ? "" : DataUrl(_image, "image/png"))
            .String("attachment", _attachment == null ? "" : DataUrl(_attachment, _attachmentMime!))
            .Array("contacts", (_state?.Contacts ?? []).Select(contact => new TguiData().Number("id", contact.Id)
                .String("name", contact.Name).Bool("available", contact.Available).Bool("unread", contact.Unread)))
            .Array("messages", (_state?.Messages ?? []).Select(message => new TguiData().Number("id", message.Id)
                .Bool("outgoing", message.Outgoing).String("text", message.Text).Bool("hasImage", message.HasImage)));
        _panel.SetIncrementalState("PdaMessenger", data, Label("title"));
        if (_panel.UsingNative)
            DrawNative();
    }

    private static string DataUrl(byte[] bytes, string mime) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    private void DrawNative()
    {
        if (_native == null)
            return;

        var draft = _nativeInput?.Text ?? "";
        _native.RemoveAllChildren();
        _native.AddChild(Button("refresh", () => Request(_state == null ? PdaMessengerAction.Open : PdaMessengerAction.Refresh)));
        if (_state == null)
        {
            _native.AddChild(new Label { Text = Label("waiting") });
            return;
        }

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

        foreach (var contact in _state.Contacts)
        {
            var button = new Button { Text = (contact.Unread ? "● " : "") + contact.Name };
            button.OnPressed += _ => Request(PdaMessengerAction.Select, contact.Id);
            contacts.AddChild(button);
        }

        foreach (var message in _state.Messages)
        {
            if (message.Text.Length > 0)
            {
                var text = new RichTextLabel();
                text.SetMessage(FormattedMessage.FromUnformatted(
                    (message.Outgoing ? Label("you") + " " : "") + message.Text));
                history.AddChild(text);
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

        _native.AddChild(new Label { Text = _localStatus.Length > 0 ? _localStatus : _state.Status });
        if (_state.Peer <= 0)
            return;

        var composer = new BoxContainer { SeparationOverride = 4 };
        _native.AddChild(composer);
        var input = new LineEdit { Text = draft, PlaceHolder = Label("write"), HorizontalExpand = true };
        _nativeInput = input;
        input.OnTextEntered += _ => Send(input.Text);
        composer.AddChild(input);
        if (_state.Attachments)
            composer.AddChild(Button(_attachment == null ? "file" : "remove", () =>
            {
                if (_attachment == null) OpenFile();
                else { _attachment = null; _attachmentMime = null; Publish(); }
            }));
        composer.AddChild(Button("send", () => Send(input.Text)));
    }

    private Button Button(string key, Action action)
    {
        var button = new Button { Text = Label(key) };
        button.OnPressed += _ => action();
        return button;
    }
}
