using Content.Client.UserInterface.Controls;
using System.Numerics;
using Robust.Shared.Console;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Client._Mono.Company;
using Content.Client.Players.PlayTimeTracking;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Developer preview. Actual game actions use WebUiBoundUserInterface.</summary>
// Admins with the DEBUG flag only: client commands get their flags from Resources/clientCommandPerms.yml
// (AdminCommandAttribute is server-side).
public sealed class WebUiPreviewCommand : IConsoleCommand
{
    public string Command => "tgui_preview";
    public string Description => "Preview a packaged TGUI interface, chat or the permitted wiki window.";
    public string Help => "tgui_preview <DeepLagoonDemo|character|chat|wiki>";
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1) { shell.WriteLine(Help); return; }
#if DEBUG
        if (args[0] is "character-wait" or "editor-wait")
        {
            PreviewWhenConnected(shell, 0, args[0] == "editor-wait");
            return;
        }
#endif
        if (args[0] == "character")
        {
            var preferences = IoCManager.Resolve<IClientPreferencesManager>();
            if (preferences.Preferences == null || IoCManager.Resolve<IBaseClient>().RunLevel != ClientRunLevel.InGame)
            {
                shell.WriteLine("Connect to a server lobby before previewing the character editor.");
                return;
            }
            var editor = new HumanoidProfileEditor(preferences,
                IoCManager.Resolve<IConfigurationManager>(), IoCManager.Resolve<IEntityManager>(),
                IoCManager.Resolve<IFileDialogManager>(), IoCManager.Resolve<ILogManager>(),
                IoCManager.Resolve<IPlayerManager>(), IoCManager.Resolve<IPrototypeManager>(),
                IoCManager.Resolve<IResourceCache>(), IoCManager.Resolve<JobRequirementsManager>(),
                IoCManager.Resolve<MarkingManager>(), IoCManager.Resolve<CompanyManager>());
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), 0);
            var preview = new FancyWindow { Title = "Character editor preview (draft only)", SetSize = new Vector2(1100, 800) };
            preview.ContentsContainer.AddChild(editor);
            preview.OnClose += preview.Dispose;
            preview.OpenCentered();
            return;
        }
        if (args[0] == "wiki")
        {
            var wiki = new WikiGuidebookWindow();
            wiki.OnClose += wiki.Dispose;
            wiki.OpenCentered();
            return;
        }
        if (args[0] == "chat")
        {
            var preview = new FancyWindow { Title = "TGUI chat preview", SetSize = new Vector2(650, 500) };
            preview.OnClose += preview.Dispose;
            var chat = new GameWebView(chat: true);
            preview.ContentsContainer.AddChild(chat);
            chat.Ready += () => chat.Send("chat/message", "{\"type\":\"localchat\",\"text\":\"DeepLagoon: Привет!\",\"html\":\"<span class=\\\"say\\\">DeepLagoon: Привет!</span>\"}");
            preview.OpenCentered();
            return;
        }
        var window = new WebUiWindow { Title = "TGUI preview (no server actions)" };
        window.OnClose += window.Dispose;
        window.Web.Message += (type, _) => { if (type == "close") window.Close(); };
        window.Panel.SetState(args[0], "{\"count\":0}");
        window.OpenCentered();
    }

#if DEBUG
    private void PreviewWhenConnected(IConsoleShell shell, int attempt, bool floating = false)
    {
        var preferences = IoCManager.Resolve<IClientPreferencesManager>();
        if (preferences.ServerDataLoaded && IoCManager.Resolve<IBaseClient>().RunLevel == ClientRunLevel.InGame)
        {
            if (floating) IoCManager.Resolve<IUserInterfaceManager>().GetUIController<LobbyUIController>().OpenCharacterEditor();
            else Execute(shell, "character", new[] { "character" });
        }
        else if (attempt < 240)
            Timer.Spawn(1000, () => PreviewWhenConnected(shell, attempt + 1, floating));
        else
            shell.WriteLine("Character preview timed out waiting for a connected lobby.");
    }
#endif
}
