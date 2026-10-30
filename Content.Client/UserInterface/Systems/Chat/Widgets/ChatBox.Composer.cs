using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Systems.Chat.Controls;
using Content.Shared.Chat;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Input;
using Robust.Shared.Input;

namespace Content.Client.UserInterface.Systems.Chat.Widgets;

public partial class ChatBox
{
    private ComposerWindow? _composer;
    private ComposerWindow? _composerWindow;
    private int _composerSession;
    private int _composerRevision;
    private IInputManager? _composerInput;
    private bool _composerComposing;

    private sealed class ComposerWindow : BaseWindow
    {
        public readonly TguiPanel Panel = new() { WindowControlsInset = new(20, 20) };
        private bool? _emote;
        public ComposerWindow()
        {
            Resizable = false;
            // Keep layout and browser startup active, but hide the blank bootstrap frame.
            Modulate = Color.Transparent;
            SetSize = new Vector2(440, 190);
            AddChild(Panel);
        }

        public void SetEmoteMode(bool emote)
        {
            if (_emote == emote || TguiWindowControls.IsMaximized(this)) return;
            _emote = emote;
            var size = emote ? new Vector2(840, 460) : new Vector2(440, 190);
            if (Parent is not { } root) { SetSize = size; return; }
            size = Vector2.Min(size, root.Size);
            var position = Position + (Size - size) / 2;
            SetSize = size;
            LayoutContainer.SetPosition(this, Vector2.Clamp(position, Vector2.Zero, Vector2.Max(Vector2.Zero, root.Size - size)));
        }
    }

    private void OpenComposer()
    {
        if (_composer == null)
        {
            _composerSession++;
            var window = _composerWindow ??= new ComposerWindow();
            window.Modulate = Color.Transparent;
            var available = UserInterfaceManager.WindowRoot.Size;
            if (available.X > 0 && available.Y > 0)
                window.SetSize = Vector2.Min(window.SetSize, available);
            _composer = window;
            _composerInput = IoCManager.Resolve<IInputManager>();
            _composerInput.FirstChanceOnKeyEvent += OnComposerKey;
            _controller.SelectableChannelsChanged += OnComposerChannelsChanged;
            window.Panel.OnAction += HandleComposerAction;
            window.Panel.OnClose += CloseComposer;
            window.OnClose += CloseComposer;
            window.Panel.Web.Ready += window.Panel.Web.FocusTextInput;
            window.Panel.Web.Failed += OnComposerFailed;
            window.Panel.SetNativeFallback(OnComposerFailed);
            window.OpenCentered();
        }
        _composerRevision++;
        PublishComposer();
        _composer.Panel.Web.FocusTextInput();
        _controller.NotifyChatFocus(this, true);
    }

    private void PublishComposer()
    {
        // Permissions can change while the composer is closed. Never publish
        // a selection missing from its options, including on the first open.
        var selected = _controller.MapLocalIfGhost(SelectedChannel);
        if (!ChannelSelectorPopup.ChannelSelectorOrder.Contains(selected) ||
            (_controller.SelectableChannels & selected) == 0)
            selected = ChatSelectChannel.OOC;
        if (selected != SelectedChannel)
            SafelySelectChannel(selected);

        _composer?.SetEmoteMode(SelectedChannel == ChatSelectChannel.Emotes);
        var channels = ChannelSelectorPopup.ChannelSelectorOrder
            .Where(channel => (_controller.SelectableChannels & channel) != 0)
            .Select(channel => "{\"id\":" + GameWebView.Quote(((uint) channel).ToString()) +
                ",\"name\":" + GameWebView.Quote(ChannelSelectorButton.ChannelSelectorName(channel)) + "}");
        _composer?.Panel.SetState("ChatComposer", "{\"session\":" + _composerSession + ",\"revision\":" + _composerRevision + ",\"text\":" + GameWebView.Quote(ChatInput.Input.Text) +
            ",\"channel\":" + GameWebView.Quote(((uint) SelectedChannel).ToString()) +
            ",\"emoteChannel\":" + GameWebView.Quote(((uint) ChatSelectChannel.Emotes).ToString()) +
            ",\"maxLength\":" + _controller.MaxMessageLength +
            ",\"channels\":[" + string.Join(",", channels) + "]}", Loc.GetString("dl-chat-composer-title"));
    }

    private void OnComposerChannelsChanged(ChatSelectChannel channels)
    {
        PublishComposer();
    }

    private void HandleComposerAction(string action, string payload)
    {
        if (_composer == null || !TguiActionData.TryParse(payload, out var envelope) ||
            !envelope!.TryInt("session", out var session) || session != _composerSession) return;
        if (action == "presented")
        {
            if (_composer != null) _composer.Modulate = Color.White;
            return;
        }
        if (action == "focus")
        {
            _composerComposing = false;
            _composer?.Panel.Web.FocusTextInput();
            _controller.NotifyChatFocus(this, true);
            return;
        }
        if (action == "blur")
        {
            _controller.NotifyChatFocus(this, false);
            return;
        }
        if (action == "composition")
        {
            if (TguiActionData.TryParse(payload, out var composition))
                _composerComposing = composition!.String("active") == "true";
            return;
        }
        if (action is not ("draft" or "channel" or "cycle" or "submit" or "cancel")) return;
        if (TguiActionData.TryParse(payload, out var args) && args!.String("text") is { } text)
        {
            if (text.Length > _controller.MaxMessageLength) return;
            ChatInput.Input.SetText(text, invokeEvent: action == "draft");
            if (action == "cycle")
            {
                if (_controller.SelectableChannels == 0) return;
                CycleChatChannel(false);
                _composerRevision++;
            }
            if (action is "channel" or "submit")
            {
                if (!uint.TryParse(args.String("channel"), out var id)) return;
                var channel = (ChatSelectChannel) id;
                if (!ChannelSelectorPopup.ChannelSelectorOrder.Contains(channel) ||
                    (_controller.SelectableChannels & channel) == 0) return;
                // Channel selection strips an old explicit prefix; submission
                // preserves prefixes for the existing server chat routing.
                if (action == "channel")
                {
                    SafelySelectChannel(channel);
                    _composerRevision++;
                }
                else
                {
                    _controller.SendMessage(this, channel);
                    CloseComposer();
                    return;
                }
            }
            if (action == "cancel") { CloseComposer(); return; }
            PublishComposer();
        }
    }

    private void OnComposerKey(KeyEventArgs args, KeyEventType type)
    {
        if (Content.Client._DeepLagoon.WebUI.WebViewCapabilities.NativeLifecycle)
            return;
        var tab = args.Key == Keyboard.Key.Tab && !args.Shift && !args.Control && !args.Alt;
        var enter = args.Key is Keyboard.Key.Return or Keyboard.Key.NumpadEnter;
        if (args.Handled || type == KeyEventType.Up || (!enter && !tab) ||
            _composerComposing || _composer?.Panel.Web is not { IsReady: true, HasInputFocus: true } web)
            return;
        args.Handle();
        if (type == KeyEventType.Down) web.SendInputKey(tab, args.Shift);
    }

    private void CloseComposer()
    {
        if (_composer is not { } window) return;
        _composer = null;
        if (_composerInput != null) _composerInput.FirstChanceOnKeyEvent -= OnComposerKey;
        _composerInput = null;
        _composerComposing = false;
        _controller.SelectableChannelsChanged -= OnComposerChannelsChanged;
        window.Panel.OnAction -= HandleComposerAction;
        window.Panel.OnClose -= CloseComposer;
        window.OnClose -= CloseComposer;
        window.Panel.Web.Ready -= window.Panel.Web.FocusTextInput;
        window.Panel.Web.Failed -= OnComposerFailed;
        window.Panel.Web.ReleaseTextInput();
        window.Close();
        _controller.NotifyChatFocus(this, false);
    }

    private void OnComposerFailed()
    {
        DisposeComposer();
        SetChatMode(true, true);
        ChatInput.Input.GrabKeyboardFocus();
    }

    private void DisposeComposer()
    {
        CloseComposer();
        _composerWindow?.Dispose();
        _composerWindow = null;
    }
}
