using System.Text;
using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Utility;

namespace Content.Server.Administration.Commands;

[AnyCommand]
public sealed class AdminWhoCommand : IConsoleCommand
{
    public string Command => "adminwho";
    public string Description => "Returns a list of all admins on the server";
    public string Help => "Usage: adminwho";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var adminMgr = IoCManager.Resolve<IAdminManager>();

        var seeStealth = true;

        // If null it (hopefully) means it is being called from the console.
        if (shell.Player != null)
        {
            var playerData = adminMgr.GetAdminData(shell.Player);

            seeStealth = playerData != null && playerData.CanStealth();
        }

        var sb = new StringBuilder();
        var count = 0;
        foreach (var admin in adminMgr.ActiveAdmins)
        {
            var adminData = adminMgr.GetAdminData(admin)!;
            DebugTools.AssertNotNull(adminData);

            if (adminData.Stealth && !seeStealth)
                continue;

            if (count > 0)
                sb.Append('\n');
            count++;

            sb.Append(admin.Name);
            if (adminData.Title is { } title)
                sb.Append($": [{title}]");
        }

        if (count > 0)
            sb.Append('\n');
        sb.Append(Loc.GetString("cmd-adminwho-total", ("count", count)));

        shell.WriteLine(sb.ToString());
    }
}
