using Content.Client._NF.LateJoin;
using Content.Client.Audio;
using Content.Client.Eui;
using Content.Client.GameTicking.Managers;
using Content.Client.Lobby.UI;
using Content.Client.Message;
using Content.Client.UserInterface.Systems.Chat;
using Content.Client.Voting;
using Content.Shared.Audio;
using Robust.Shared.Prototypes;
using System.Linq;
using Content.Shared.CCVar;
using Robust.Client;
using Robust.Client.Console;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;
using PickerWindow = Content.Client._NF.LateJoin.Windows.PickerWindow;

namespace Content.Client.Lobby
{
    public sealed partial class LobbyState : Robust.Client.State.State
    {
        [Dependency] private IBaseClient _baseClient = default!;
        [Dependency] private IConfigurationManager _cfg = default!;
        [Dependency] private IClientConsoleHost _consoleHost = default!;
        [Dependency] private IEntityManager _entityManager = default!;
        [Dependency] private IResourceCache _resourceCache = default!;
        [Dependency] private IUserInterfaceManager _userInterfaceManager = default!;
        [Dependency] private IGameTiming _gameTiming = default!;
        [Dependency] private IVoteManager _voteManager = default!;
        [Dependency] private IPrototypeManager _prototypes = default!;

        private ClientGameTicker _gameTicker = default!;
        private ContentAudioSystem _contentAudioSystem = default!;

        protected override Type? LinkedScreenType { get; } = typeof(LobbyGui);
        public LobbyGui? Lobby;

        // Frontier - save pickerwindow so it opens only once
        private PickerWindow? _pickerWindow = null;

        protected override void Startup()
        {
            if (_userInterfaceManager.ActiveScreen == null)
            {
                return;
            }

            Lobby = (LobbyGui) _userInterfaceManager.ActiveScreen;
            Lobby.Background.Start();

            var chatController = _userInterfaceManager.GetUIController<ChatUIController>();
            _gameTicker = _entityManager.System<ClientGameTicker>();
            _contentAudioSystem = _entityManager.System<ContentAudioSystem>();
            _contentAudioSystem.LobbySoundtrackChanged += UpdateLobbySoundtrackInfo;
            Lobby.MusicPlayer.Previous.OnPressed += OnPreviousTrack;
            Lobby.MusicPlayer.Next.OnPressed += OnNextTrack;
            Lobby.MusicPlayer.Pause.OnPressed += OnPauseMusic;

            chatController.SetMainChat(true);

            _voteManager.SetPopupContainer(Lobby.VoteContainer);
            LayoutContainer.SetAnchorPreset(Lobby, LayoutContainer.LayoutPreset.Wide);

            var lobbyNameCvar = _cfg.GetCVar(CCVars.ServerLobbyName);
            var serverName = _baseClient.GameInfo?.ServerName ?? string.Empty;

            Lobby.ServerName.Text = string.IsNullOrEmpty(lobbyNameCvar)
                ? Loc.GetString("ui-lobby-title", ("serverName", serverName))
                : lobbyNameCvar;

            var width = _cfg.GetCVar(CCVars.ServerLobbyRightPanelWidth);
            Lobby.MainContainer.SidePanelWidth = width;

            UpdateLobbyUi();

            Lobby.CharacterPreview.CharacterSetupButton.OnPressed += OnSetupPressed;
            Lobby.ReadyButton.OnPressed += OnReadyPressed;
            Lobby.ReadyButton.OnToggled += OnReadyToggled;

            _gameTicker.InfoBlobUpdated += UpdateLobbyUi;
            _gameTicker.LobbyStatusUpdated += LobbyStatusUpdated;
            _gameTicker.LobbyLateJoinStatusUpdated += LobbyLateJoinStatusUpdated;
        }

        protected override void Shutdown()
        {
            // Close the background browser before switching to gameplay, including observing.
            Lobby?.Background.Stop();
            var chatController = _userInterfaceManager.GetUIController<ChatUIController>();
            chatController.SetMainChat(false);
            _gameTicker.InfoBlobUpdated -= UpdateLobbyUi;
            _gameTicker.LobbyStatusUpdated -= LobbyStatusUpdated;
            _gameTicker.LobbyLateJoinStatusUpdated -= LobbyLateJoinStatusUpdated;
            _contentAudioSystem.LobbySoundtrackChanged -= UpdateLobbySoundtrackInfo;
            Lobby!.MusicPlayer.Previous.OnPressed -= OnPreviousTrack;
            Lobby!.MusicPlayer.Next.OnPressed -= OnNextTrack;
            Lobby!.MusicPlayer.Pause.OnPressed -= OnPauseMusic;

            _voteManager.ClearPopupContainer();

            Lobby!.CharacterPreview.CharacterSetupButton.OnPressed -= OnSetupPressed;
            Lobby!.ReadyButton.OnPressed -= OnReadyPressed;
            Lobby!.ReadyButton.OnToggled -= OnReadyToggled;

            Lobby = null;
        }

        public void SwitchState(LobbyGui.LobbyGuiState state)
        {
            // Yeah I hate this but LobbyState contains all the badness for now.
            Lobby?.SwitchState(state);
        }

        private void OnSetupPressed(BaseButton.ButtonEventArgs args)
        {
            SetReady(false);
            Lobby?.SwitchState(LobbyGui.LobbyGuiState.CharacterSetup);
        }

        private void OnReadyPressed(BaseButton.ButtonEventArgs args)
        {
            if (!_gameTicker.IsGameStarted)
            {
                return;
            }
            // Frontier to downstream: if you want to skip the first window and go straight to station picker,
            // simply change the enum to station or crew in the PickerWindow constructor.
            _pickerWindow ??= new PickerWindow();
            _pickerWindow.OpenCentered();
        }

        private void OnReadyToggled(BaseButton.ButtonToggledEventArgs args)
        {
            SetReady(args.Pressed);
        }

        public override void FrameUpdate(FrameEventArgs e)
        {
            if (_gameTicker.IsGameStarted)
            {
                Lobby!.StartTime.Text = string.Empty;
                var roundTime = _gameTiming.CurTime.Subtract(_gameTicker.RoundStartTimeSpan);
                Lobby!.StationTime.Text = Loc.GetString("lobby-state-player-status-round-time", ("hours", roundTime.Hours), ("minutes", roundTime.Minutes));
                Lobby.RoundCountdown.Text = $"{(int) roundTime.TotalHours:00}:{roundTime.Minutes:00}";
                Lobby.RoundCountdownCaption.Text = Loc.GetString("dl-lobby-round-time-caption");
                return;
            }

            Lobby!.StationTime.Text = Loc.GetString("lobby-state-player-status-round-not-started");
            Lobby.RoundCountdown.Text = "--:--";
            Lobby.RoundCountdownCaption.Text = Loc.GetString("dl-lobby-countdown-caption");
            string text;

            if (_gameTicker.Paused)
            {
                text = Loc.GetString("lobby-state-paused");
                Lobby.RoundCountdownCaption.Text = Loc.GetString("dl-lobby-paused-caption");
            }
            else if (_gameTicker.StartTime < _gameTiming.CurTime)
            {
                Lobby!.StartTime.Text = Loc.GetString("lobby-state-soon");
                Lobby.RoundCountdownCaption.Text = Loc.GetString("dl-lobby-starting-caption");
                return;
            }
            else
            {
                var difference = _gameTicker.StartTime - _gameTiming.CurTime;
                var seconds = difference.TotalSeconds;
                if (seconds < 0)
                {
                    text = Loc.GetString(seconds < -5 ? "lobby-state-right-now-question" : "lobby-state-right-now-confirmation");
                }
                else if (difference.TotalHours >= 1)
                {
                    text = $"{Math.Floor(difference.TotalHours)}:{difference.Minutes:D2}:{difference.Seconds:D2}";
                }
                else
                {
                    text = $"{difference.Minutes:D2}:{difference.Seconds:D2}";
                }
                Lobby.RoundCountdown.Text = text;
            }

            Lobby!.StartTime.Text = Loc.GetString("lobby-state-round-start-countdown-text", ("timeLeft", text));
        }

        private void LobbyStatusUpdated()
        {
            UpdateLobbyBackground();
            UpdateLobbyUi();
        }

        private void LobbyLateJoinStatusUpdated()
        {
            Lobby!.ReadyButton.Disabled = _gameTicker.DisallowedLateJoin;
        }

        private void UpdateLobbyUi()
        {
            if (_gameTicker.IsGameStarted)
            {
                Lobby!.ReadyButton.Text = Loc.GetString("lobby-state-ready-button-join-state");
                Lobby!.ReadyButton.ToggleMode = false;
                Lobby!.ReadyButton.Disabled = _gameTicker.DisallowedLateJoin;
                Lobby!.ReadyButton.Pressed = false;
                Lobby!.ObserveButton.Disabled = false;
            }
            else
            {
                Lobby!.StartTime.Text = string.Empty;
                Lobby!.ReadyButton.ToggleMode = true;
                Lobby!.ReadyButton.Disabled = false;
                Lobby!.ReadyButton.Pressed = _gameTicker.AreWeReady;
                Lobby!.ReadyButton.Text = Loc.GetString(Lobby!.ReadyButton.Pressed ? "lobby-state-player-status-ready": "lobby-state-player-status-not-ready");
                Lobby!.ObserveButton.Disabled = true;
            }

            if (_gameTicker.ServerInfoBlob != null)
            {
                Lobby!.SetServerInfo(_gameTicker.ServerInfoBlob);
            }
        }

        private void UpdateLobbySoundtrackInfo(LobbySoundtrackChangedEvent ev)
        {
            if (ev.SoundtrackFilename == null)
            {
                Lobby!.MusicPlayer.SetTrack(null, null, null, _contentAudioSystem.CanSwitchLobbyTrack,
                    _contentAudioSystem.IsLobbyMusicPaused, _contentAudioSystem.CanPauseLobbyMusic);
            }
            else if (
                ev.SoundtrackFilename != null
                && _resourceCache.TryGetResource<AudioResource>(ev.SoundtrackFilename, out var lobbySongResource)
                )
            {
                var lobbyStream = lobbySongResource.AudioStream;
                var metadata = _prototypes.EnumeratePrototypes<LobbyMusicPrototype>()
                    .FirstOrDefault(track => track.Path.ToString() == ev.SoundtrackFilename);

                var trackTitle = metadata?.Name ?? lobbyStream.Title;
                var trackArtist = metadata?.Artist ?? lobbyStream.Artist;
                var title = string.IsNullOrEmpty(trackTitle)
                    ? Loc.GetString("lobby-state-song-unknown-title")
                    : trackTitle;

                var artist = string.IsNullOrEmpty(trackArtist)
                    ? Loc.GetString("lobby-state-song-unknown-artist")
                    : trackArtist;

                Lobby!.MusicPlayer.SetTrack(title, artist, metadata?.Cover, _contentAudioSystem.CanSwitchLobbyTrack,
                    _contentAudioSystem.IsLobbyMusicPaused, _contentAudioSystem.CanPauseLobbyMusic);
            }
        }

        private void OnPreviousTrack(BaseButton.ButtonEventArgs args) => _contentAudioSystem.SwitchLobbyTrack(-1);
        private void OnNextTrack(BaseButton.ButtonEventArgs args) => _contentAudioSystem.SwitchLobbyTrack(1);
        private void OnPauseMusic(BaseButton.ButtonEventArgs args) => _contentAudioSystem.ToggleLobbyMusicPaused();

        private void UpdateLobbyBackground()
        {
            Lobby!.Background.SetSource(_gameTicker.LobbyBackground);
        }

        private void SetReady(bool newReady)
        {
            if (_gameTicker.IsGameStarted)
            {
                return;
            }

            _consoleHost.ExecuteCommand($"toggleready {newReady}");
        }
    }
}
