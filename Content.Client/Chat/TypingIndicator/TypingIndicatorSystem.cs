using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Chat.TypingIndicator;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Client.Chat.TypingIndicator;

// Client-side typing system tracks user input in chat box
public sealed partial class TypingIndicatorSystem : SharedTypingIndicatorSystem
{
    [Dependency] private IGameTiming _time = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IConfigurationManager _cfg = default!;

    private readonly TimeSpan _typingTimeout = TimeSpan.FromSeconds(5);
    private TimeSpan _lastTextChange;
    private bool _isClientTyping;
    private bool _isClientChatFocused;
    private ChatSelectChannel _channel = ChatSelectChannel.Local;
    private EntityUid? _lastEntity;
    private TypingIndicatorState _lastState;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, CCVars.ChatShowTypingIndicator, OnShowTypingChanged);
    }

    public void ClientChangedChatText(bool hasText = true)
    {
        // don't update it if player don't want to show typing indicator
        if (!_cfg.GetCVar(CCVars.ChatShowTypingIndicator))
            return;

        // client typed something - show typing indicator
        _isClientTyping = hasText;
        _lastTextChange = _time.RealTime;
        ClientUpdateTyping();
    }

    public void ClientSubmittedChatText()
    {
        // client submitted text - hide typing indicator
        _isClientTyping = false;
        _isClientChatFocused = false;
        ClientUpdateTyping();
    }

    public void ClientChangedChatFocus(bool isFocused)
    {
        _isClientChatFocused = isFocused;
        if (!isFocused)
            _isClientTyping = false;
        ClientUpdateTyping();
    }

    public void ClientChangedChatChannel(ChatSelectChannel channel)
    {
        if (_channel == channel)
            return;

        _channel = channel;
        _isClientTyping = false;
        ClientUpdateTyping();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // This state tracks UI input, rather than predicted entity state. Do not
        // expire it while replaying an earlier tick during reconciliation.
        if (!_time.IsFirstTimePredicted)
            return;

        // check if client didn't changed chat text box for a long time
        if (_isClientTyping)
        {
            var dif = _time.RealTime - _lastTextChange;
            if (dif >= _typingTimeout)
            {
                // client didn't typed anything for a long time - change indicator
                _isClientTyping = false;
                ClientUpdateTyping();
            }
        }
    }

    private void ClientUpdateTyping()
    {
        // check if player controls any pawn
        if (_playerManager.LocalEntity is not { } entity)
        {
            _lastEntity = null;
            return;
        }

        var state = GetState(_channel, _isClientChatFocused && _cfg.GetCVar(CCVars.ChatShowTypingIndicator), _isClientTyping);
        if (_lastEntity == entity && _lastState == state)
            return;

        _lastEntity = entity;
        _lastState = state;

        // send a networked event to server
        RaiseNetworkEvent(new TypingChangedEvent(state));
    }

    public static TypingIndicatorState GetState(ChatSelectChannel channel, bool focused, bool typing)
    {
        if (!focused)
            return TypingIndicatorState.None;

        return channel switch
        {
            ChatSelectChannel.Local or ChatSelectChannel.Whisper or ChatSelectChannel.Radio =>
                typing ? TypingIndicatorState.Typing : TypingIndicatorState.Idle,
            ChatSelectChannel.Emotes => typing ? TypingIndicatorState.EmoteTyping : TypingIndicatorState.EmoteIdle,
            ChatSelectChannel.LOOC => typing ? TypingIndicatorState.LoocTyping : TypingIndicatorState.LoocIdle,
            _ => TypingIndicatorState.None,
        };
    }

    private void OnShowTypingChanged(bool showTyping)
    {
        // hide typing indicator immediately if player don't want to show it anymore
        if (!showTyping)
            _isClientTyping = false;
        ClientUpdateTyping();
    }
}
