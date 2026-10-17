using System.Numerics;
using Content.Client._DeepLagoon.Performance;
using Content.Client.Changelog;
using Content.Client.Hands;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Screens;
using Content.Client.UserInterface.Systems.Actions.Widgets;
using Content.Client.UserInterface.Systems.Gameplay;
using Content.Client.Viewport;
using Content.Shared.CCVar;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;

namespace Content.Client.Gameplay
{
    [Virtual]
    public partial class GameplayState : GameplayStateBase, IMainViewportState
    {
        [Dependency] private IEyeManager _eyeManager = default!;
        [Dependency] private IOverlayManager _overlayManager = default!;
        [Dependency] private IUserInterfaceManager _uiManager = default!;
        [Dependency] private ChangelogManager _changelog = default!;
        [Dependency] private IConfigurationManager _configurationManager = default!;

        private PerformanceCounter _fpsCounter = default!;
        private Label _version = default!;

        public MainViewport Viewport => _uiManager.ActiveScreen!.GetWidget<MainViewport>()!;

        private readonly GameplayStateLoadController _loadController;

        public GameplayState()
        {
            IoCManager.InjectDependencies(this);

            _loadController = _uiManager.GetUIController<GameplayStateLoadController>();
        }

        protected override void Startup()
        {
            base.Startup();

            LoadMainScreen();
            _configurationManager.OnValueChanged(CCVars.UILayout, ReloadMainScreenValueChange);
            _configurationManager.OnValueChanged(Content.Shared._DeepLagoon.WebUI.WebUiCVars.VanillaChat, ReloadChatMode, false);

            // Add the hand-item overlay.
            _overlayManager.AddOverlay(new ShowHandItemOverlay());

            // FPS counter.
            // yeah this can just stay here, whatever
            _fpsCounter = new PerformanceCounter();
            _fpsCounter.OnResized += UpdateActionsCounterOffset;
            UserInterfaceManager.PopupRoot.AddChild(_fpsCounter);
            _configurationManager.OnValueChanged(CCVars.HudFpsCounterVisible, OnFpsCounterVisibilityChanged, true);

            // Version number watermark.
            _version = new Label();
            _version.FontColorOverride = Color.FromHex("#FFFFFF20");
            _version.Text = _changelog.GetClientVersion();
            UserInterfaceManager.PopupRoot.AddChild(_version);
            _configurationManager.OnValueChanged(CCVars.HudVersionWatermark, (show) => { _version.Visible = VersionVisible(); }, true);
            _configurationManager.OnValueChanged(CCVars.ForceClientHudVersionWatermark, (show) => { _version.Visible = VersionVisible(); }, true);
            // TODO make this centered or something
            LayoutContainer.SetPosition(_version, new Vector2(70, 0));
        }

        // This allows servers to force the watermark on clients
        private bool VersionVisible()
        {
            var client = _configurationManager.GetCVar(CCVars.HudVersionWatermark);
            var server = _configurationManager.GetCVar(CCVars.ForceClientHudVersionWatermark);
            return client || server;
        }

        protected override void Shutdown()
        {
            _overlayManager.RemoveOverlay<ShowHandItemOverlay>();

            base.Shutdown();
            // Clear viewport to some fallback, whatever.
            _eyeManager.MainViewport = UserInterfaceManager.MainViewport;
            _fpsCounter.OnResized -= UpdateActionsCounterOffset;
            _fpsCounter.Dispose();
            _configurationManager.UnsubValueChanged(CCVars.HudFpsCounterVisible, OnFpsCounterVisibilityChanged);
            _uiManager.ClearWindows();
            _configurationManager.UnsubValueChanged(CCVars.UILayout, ReloadMainScreenValueChange);
            _configurationManager.UnsubValueChanged(Content.Shared._DeepLagoon.WebUI.WebUiCVars.VanillaChat, ReloadChatMode);
            UnloadMainScreen();
        }

        private void OnFpsCounterVisibilityChanged(bool show)
        {
            _fpsCounter.Visible = show;
            UpdateActionsCounterOffset();
        }

        private void UpdateActionsCounterOffset()
        {
            if (_fpsCounter == null || _uiManager.GetActiveUIWidgetOrNull<ActionsBar>() is not { } actions)
                return;

            // Leave room for the counter, including before its first text measurement.
            var top = _fpsCounter.Visible ? Math.Max(20, _fpsCounter.Height) + 4 : 0;
            actions.Margin = new Thickness(0, top, 0, 0);
        }

        private void ReloadMainScreenValueChange(string _)
        {
            ReloadMainScreen();
        }

        private void ReloadChatMode(bool _)
        {
            var draft = (_uiManager.ActiveScreen as InGameScreen)?.ChatBox.ChatInput.Input.Text;
            var channel = (_uiManager.ActiveScreen as InGameScreen)?.ChatBox.SelectedChannel;
            ReloadMainScreen();
            if (draft != null && _uiManager.ActiveScreen is InGameScreen screen)
            {
                if (channel != null) screen.ChatBox.ChatInput.ChannelSelector.Select(channel.Value);
                screen.ChatBox.ChatInput.Input.SetText(draft);
            }
        }

        public void ReloadMainScreen()
        {
            if (_uiManager.ActiveScreen?.GetWidget<MainViewport>() == null)
            {
                return;
            }

            UnloadMainScreen();
            LoadMainScreen();
        }

        private void UnloadMainScreen()
        {
            _loadController.UnloadScreen();
            _uiManager.UnloadScreen();
        }

        private void LoadMainScreen()
        {
            var screenTypeString = _configurationManager.GetCVar(CCVars.UILayout);
            if (!Content.Client._DeepLagoon.WebUI.TguiPanel.Enabled ||
                _configurationManager.GetCVar(Content.Shared._DeepLagoon.WebUI.WebUiCVars.VanillaChat))
                screenTypeString = nameof(ScreenType.Default);
            if (!Enum.TryParse(screenTypeString, out ScreenType screenType))
            {
                screenType = default;
            }

            switch (screenType)
            {
                case ScreenType.Default:
                    _uiManager.LoadScreen<DefaultGameScreen>();
                    break;
                case ScreenType.Separated:
                    _uiManager.LoadScreen<SeparatedChatGameScreen>();
                    break;
            }

            _loadController.LoadScreen();
            UpdateActionsCounterOffset();
        }

        protected override void OnKeyBindStateChanged(ViewportBoundKeyEventArgs args)
        {
            if (args.Viewport == null)
                base.OnKeyBindStateChanged(new ViewportBoundKeyEventArgs(args.KeyEventArgs, Viewport.Viewport));
            else
                base.OnKeyBindStateChanged(args);
        }
    }
}
