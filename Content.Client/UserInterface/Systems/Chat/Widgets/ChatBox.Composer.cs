using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Systems.Chat.Controls;
using Content.Shared.Chat;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.Input;
using Robust.Shared.Input;

namespace Content.Client.UserInterface.Systems.Chat.Widgets;

public partial class ChatBox
{
    private ComposerWindow? _composer;
    // DeepLagoon: tgui fix (Н3). One composer browser per chat box, kept alive between openings: keys typed
    // right after T reach a loaded page, and closing does not dispose a browser that CEF still queries (К1).
    private ComposerWindow? _composerWindow;
    private bool _composerPresentOnFocus;
    private int _composerRevision;
    private IInputManager? _composerInput;
    private bool _composerComposing;

    // One fixed browser surface: animated form dimensions never resize CEF or
    // allocate a new native texture. Closing releases this extra browser.
    private sealed class ComposerWindow : BaseWindow
    {
        public readonly TguiPanel Panel = new(keepAlive: true); // DeepLagoon: tgui fix (Н3)
        public ComposerWindow()
        {
            Resizable = false;
            // Keep layout and browser startup active, but hide the blank bootstrap frame.
            Modulate = Color.Transparent;
            SetSize = new Vector2(880, 500);
            AddChild(Panel);
        }
    }

    private void OpenComposer()
    {
        if (_composer == null)
        {
            var window = _composerWindow ??= new ComposerWindow(); // DeepLagoon: tgui fix (Н3)
            // A reused page shows the last draft until it gets the new state; its "focus" reply presents it.
            _composerPresentOnFocus = window.Panel.Web.IsReady;
            if (_composerPresentOnFocus) window.Modulate = Color.Transparent;
            var available = UserInterfaceManager.WindowRoot.Size;
            if (available.X > 0 && available.Y > 0)
                window.SetSize = Vector2.Min(new Vector2(880, 500), available);
            _composer = window;
            _composerInput = IoCManager.Resolve<IInputManager>();
            _composerInput.FirstChanceOnKeyEvent += OnComposerKey;
            _controller.SelectableChannelsChanged += OnComposerChannelsChanged;
            window.Panel.OnAction += HandleComposerAction;
            window.Panel.OnClose += CloseComposer;
            window.OnClose += CloseComposer;
            window.Panel.Web.Ready += window.Panel.Web.FocusTextInput;
            window.Panel.Web.Failed += OnComposerFailed;
            window.OpenCentered();
        }
        _composerRevision++;
        PublishComposer();
        _composer.Panel.Web.FocusTextInput();
        _controller.NotifyChatFocus(true);
    }

    private void PublishComposer()
    {
        var channels = ChannelSelectorPopup.ChannelSelectorOrder
            .Where(channel => (_controller.SelectableChannels & channel) != 0)
            .Select(channel => "{\"id\":" + GameWebView.Quote(((uint) channel).ToString()) +
                ",\"name\":" + GameWebView.Quote(ChannelSelectorButton.ChannelSelectorName(channel)) + "}");
        _composer?.Panel.SetState("ChatComposer", "{\"revision\":" + _composerRevision + ",\"text\":" + GameWebView.Quote(ChatInput.Input.Text) +
            ",\"channel\":" + GameWebView.Quote(((uint) SelectedChannel).ToString()) +
            ",\"emoteChannel\":" + GameWebView.Quote(((uint) ChatSelectChannel.Emotes).ToString()) +
            ",\"maxLength\":" + _controller.MaxMessageLength +
            ",\"channels\":[" + string.Join(",", channels) + "]}", Loc.GetString("dl-chat-composer-title")); // DeepLagoon: tgui fix
    }

    private void OnComposerChannelsChanged(ChatSelectChannel channels)
    {
        if ((channels & SelectedChannel) == 0)
            SafelySelectChannel(_controller.MapLocalIfGhost(SelectedChannel));
        if ((channels & SelectedChannel) == 0)
            SafelySelectChannel(ChatSelectChannel.OOC);
        PublishComposer();
    }

    private void HandleComposerAction(string action, string payload)
    {
        if (action == "presented")
        {
            if (_composer != null) _composer.Modulate = Color.White;
            return;
        }
        if (action == "focus")
        {
            if (_composerPresentOnFocus && _composer != null) { _composerPresentOnFocus = false; _composer.Modulate = Color.White; }
            _composerComposing = false;
            _composer?.Panel.Web.FocusTextInput();
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
            ChatInput.Input.SetText(text);
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
        var tab = args.Key == Keyboard.Key.Tab && args.Shift;
        if (args.Handled || type == KeyEventType.Up || (args.Key != Keyboard.Key.Return && !tab) ||
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
        // DeepLagoon: tgui fix (Н3). The window is reused; its handlers are added again on the next open.
        window.Panel.OnAction -= HandleComposerAction;
        window.Panel.OnClose -= CloseComposer;
        window.OnClose -= CloseComposer;
        window.Panel.Web.Ready -= window.Panel.Web.FocusTextInput;
        window.Panel.Web.Failed -= OnComposerFailed;
        window.Panel.Web.ReleaseTextInput();
        window.Close();
        _controller.NotifyChatFocus(false);
    }

    private void OnComposerFailed()
    {
        // DeepLagoon: tgui fix (Н3). A failed page is not kept alive for the next T.
        DisposeComposer();
        ApplyChatMode(true);
        ChatInput.Input.GrabKeyboardFocus();
    }

    // DeepLagoon: tgui fix (Н3). Closes the kept-alive composer browser for good.
    private void DisposeComposer()
    {
        CloseComposer();
        _composerWindow?.Dispose();
        _composerWindow = null;
    }
}
