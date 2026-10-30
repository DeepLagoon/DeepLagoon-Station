using Content.Client.Lobby.UI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using static Robust.Client.UserInterface.StylesheetHelpers;

namespace Content.Client.UserInterface.Systems.Chat.Widgets;

public partial class ChatBox
{
    /// <summary>Local lobby chrome; message colors, fonts, filters and saved preferences remain unchanged.</summary>
    public void ApplyLobbyStyle()
    {
        ChatWindowPanel.PanelOverride = new StyleBoxFlat { BackgroundColor = Color.Transparent };
        var layout = ChatWindowPanel.GetChild(0);
        var styleRow = layout.GetChild(0);
        var heading = new Label
        {
            Text = Loc.GetString("dl-lobby-comms-heading"),
            FontOverride = LobbyConsoleTheme.GetFont(11),
            FontColorOverride = LobbyConsoleTheme.Green,
            HorizontalExpand = true,
            ClipText = true,
            VerticalAlignment = VAlignment.Center,
        };
        styleRow.AddChild(heading);
        heading.SetPositionFirst();
        styleRow.Orphan();
        var headingPanel = new PanelContainer
        {
            Name = "LobbyChatHeading",
            PanelOverride = LobbyConsoleTheme.CreateHeadingStyle(),
        };
        headingPanel.AddChild(styleRow);
        layout.AddChild(headingPanel);
        headingPanel.SetPositionFirst();
        styleRow.Stylesheet = new Stylesheet(new StyleRule[]
        {
            Element<Button>().Prop("stylebox", new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#122019"),
                BorderColor = Color.FromHex("#314B3C"),
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 6,
                ContentMarginRightOverride = 6,
            }).Prop("font-color", LobbyConsoleTheme.Green),
        });
        ChatInput.Stylesheet = styleRow.Stylesheet;
        ChatInput.Input.StyleBoxOverride = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#111F17"),
            BorderColor = Color.FromHex("#3A6048"),
            BorderThickness = new Thickness(1),
            ContentMarginLeftOverride = 6,
            ContentMarginRightOverride = 6,
            ContentMarginTopOverride = 4,
            ContentMarginBottomOverride = 4,
        };
    }
}
