using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Art;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._DeepLagoon.Art;

public sealed class ArtEditorWindow : BaseWindow
{
    public event Action? CloseRequested;
    public bool ConfirmedClose;
    public override void Close() { if (ConfirmedClose || !Panel.Web.IsReady) base.Close(); else CloseRequested?.Invoke(); }
    public readonly TguiPanel Panel = new(inheritChatAppearance: false, captureKeyboard: true, forceTgui: true) { WindowControlsInset = new Vector2(8, 5) };
    public ArtEditorWindow()
    {
        MinSize = new Vector2(700, 520); SetSize = new Vector2(980, 720); AddChild(Panel);
    }
    protected override DragMode GetDragModeFor(Vector2 point) => point.X > Size.X - 12 && point.Y > Size.Y - 18
        ? DragMode.Bottom | DragMode.Right : DragMode.None;
}

public sealed class ArtEditorBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private ArtEditorWindow? _window;
    private ArtEditorState? _state;
    private IInputManager? _input;
    private bool _editingText;
    private int _closeRequest;
    private string _openedTablet = "";
    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<ArtEditorWindow>();
        _window.Panel.OnAction += Action;
        _window.Panel.OnClose += RequestClose;
        _window.CloseRequested += RequestClose;
        _input = IoCManager.Resolve<IInputManager>();
        _input.FirstChanceOnKeyEvent += Key;
        Push();
    }
    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not ArtEditorState art) return;
        _state = art; Push();
    }
    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is ArtDraftStatusMessage draft && _window != null)
            _window.Panel.Web.Send("art/draft", new TguiData().String("writer", draft.Writer).Number("sequence", draft.Sequence)
                .Number("epoch", draft.Epoch).Number("version", draft.Version).Bool("success", draft.Success).ToString());
        if (message is ArtTabletLinkMessage link && _window != null && link.Url != _openedTablet)
        {
            _openedTablet = link.Url;
            IoCManager.Resolve<IUriOpener>().OpenUri(link.Url);
        }
    }
    private void Push()
    {
        if (_window == null || _state == null) return;
        var labels = new TguiData();
        foreach (var key in ArtTabletLabels.Keys)
            labels.String(key, Loc.GetString("dl-art-ui-" + key));
        _window.Panel.SetState("ArtStudio", new TguiData().String("document", _state.Document).String("savedDocument", _state.SavedDocument).Bool("hasDraft", _state.HasDraft)
            .String("recoveryKey", _state.RecoveryKey).Number("draftEpoch", _state.DraftEpoch).Number("draftVersion", _state.DraftVersion)
            .String("draftWriter", _state.DraftWriter).Number("draftSequence", _state.DraftSequence)
            .Number("revision", _state.Revision).String("tool", _state.Tool).String("color", _state.Color).Number("result", _state.Result)
            .String("status", _state.Status.Length == 0 ? "" : Loc.GetString(_state.Status))
            .Number("closeRequest", _closeRequest).Number("maxLayers", ArtLimits.MaxLayers)
            .Number("maxDocument", ArtLimits.MaxDocumentChars).Number("chunkSize", ArtLimits.ChunkChars)
            .Bool("tabletAvailable", _state.TabletAvailable).Bool("tabletActive", _state.TabletActive).Number("tabletRevision", _state.TabletRevision)
            .Object("labels", labels).ToString(), Loc.GetString("dl-art-ui-editor"));
    }
    private void RequestClose() { _closeRequest++; Push(); }
    private void Action(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var data) || _state == null) return;
        if (action == "editing-text") { _editingText = data!.String("value") == "true"; return; }
        if (action == "close") { if (_window != null) _window.ConfirmedClose = true; Close(); return; }
        if (action == "discard") { SendMessage(new ArtEditorMessage("discard", "", _state.Revision)); return; }
        if (action == "tablet-revoke") { SendMessage(new ArtEditorMessage(action, "", _state.Revision)); return; }
        if (action is not ("save" or "preview" or "tablet" or "draft") || !data!.TryInt("index", out var index)
            || !data.TryInt("count", out var count) || data.String("text") is not { } text || text.Length > ArtLimits.ChunkChars) return;
        data.TryInt("sequence", out var sequence); data.TryInt("epoch", out var epoch); data.TryInt("version", out var version);
        var writer = data.String("writer") ?? "";
        if (writer.Length > 32) return;
        SendMessage(new ArtEditorMessage(action, text, _state.Revision, index, count, writer, sequence, epoch, version));
    }
    private void Key(KeyEventArgs args, KeyEventType type)
    {
        if (args.Handled || _editingText || _window is not { VisibleInTree: true } window || !window.IsAtFront()
            || !window.Panel.Web.HasInputFocus || !window.Panel.Web.IsReady) return;
        var code = args.Key switch
        {
            Keyboard.Key.Z => "KeyZ", Keyboard.Key.Y => "KeyY", Keyboard.Key.S => "KeyS",
            Keyboard.Key.B => "KeyB", Keyboard.Key.E => "KeyE", Keyboard.Key.M => "KeyM",
            Keyboard.Key.Space => "Space", Keyboard.Key.Escape => "Escape", _ => null,
        };
        if (code == null || args.Alt || args.Control && code is not ("KeyZ" or "KeyY" or "KeyS")) return;
        args.Handle();
        if (type == KeyEventType.Repeat) return;
        // This existing content helper only forwards keys to this window; no engine API changes.
        window.Panel.Web.SendGridEditorKey(code, type != KeyEventType.Up, args.Control, args.Shift, args.Alt);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && _input != null) { _input.FirstChanceOnKeyEvent -= Key; _input = null; }
        base.Dispose(disposing);
    }
}
