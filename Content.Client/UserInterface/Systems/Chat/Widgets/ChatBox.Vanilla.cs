using Robust.Client.UserInterface.Controls;

namespace Content.Client.UserInterface.Systems.Chat.Widgets;

public partial class ChatBox
{
    private OutputPanel? _vanillaContents;
    private bool _useVanillaChat;
    private bool _webChatRequested;
    private void ApplyInterfaceStyle(int _) => ApplyChatMode(_cfg.GetCVar(Content.Shared._DeepLagoon.WebUI.WebUiCVars.VanillaChat));

    private void ApplyChatMode(bool vanilla) => SetChatMode(vanilla, false);

    private void SetChatMode(bool vanilla, bool fallback)
    {
        if (!fallback) vanilla = !Content.Client._DeepLagoon.WebUI.WindowInterfaceStyle.Resolve(
            _cfg.GetCVar(Content.Shared._DeepLagoon.WebUI.WebUiCVars.WindowStyles), "Chat",
            vanilla || !Content.Client._DeepLagoon.WebUI.TguiPanel.Enabled ? 2 : 1);
        _useVanillaChat = vanilla;
        DisposeComposer();
        ChatInput.Visible = vanilla || !_webChatRequested;
        if (vanilla)
        {
            if (_webChat != null) _webChat.Visible = false;
            if (_vanillaContents == null)
            {
                _vanillaContents = new OutputPanel
                {
                    HorizontalExpand = true, VerticalExpand = true,
                    Margin = new Thickness(8, 8, 8, 4)
                };
                LayoutContainer.SetAnchorPreset(_vanillaContents, LayoutContainer.LayoutPreset.Wide);
                ChatBody.AddChild(_vanillaContents);
            }
            _vanillaContents.Visible = true;
            Contents.Visible = false;
            SearchBar.Visible = SettingsPanel.Visible = false;
            _searchText = "";
            ChatInput.UseVanillaLayout();
            ChatInput.FilterButton.Visible = true;
        }
        else
        {
            if (_vanillaContents != null)
            {
                _vanillaContents.Clear();
                _vanillaContents.Visible = false;
            }
            if (_webChatRequested)
            {
                ChatInput.UseCompactLayout();
                EnableTguiChat();
            }
            if (_webChat != null) _webChat.Visible = true;
            Contents.Visible = _webChat == null;
        }
        Repopulate();
    }
}
