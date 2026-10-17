using System.Linq;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.WebUI;

public static class CivilianDiskConsoleData
{
    // Read the existing localized controls, including their current availability.
    public static TguiData Build(string? title, string? total, string? serverLabel, bool ready,
        IEnumerable<(string Id, Button Button)> entries) => new TguiData()
        .String("title", title).String("totalLabel", total).String("serverLabel", serverLabel).Bool("ready", ready)
        .String("closeLabel", Loc.GetString("civilian-console-close"))
        .String("waitingLabel", Loc.GetString("civilian-console-waiting"))
        .Array("entries", entries.Select(entry => new TguiData().String("id", entry.Id)
            .String("label", entry.Button.Text).Bool("disabled", entry.Button.Disabled)));
}
