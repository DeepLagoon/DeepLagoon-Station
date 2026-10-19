using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;

namespace Content.Client.Research.UI;

public sealed partial class DiskConsoleMenu
{
    public CivilianConsoleHost? ConsoleHost { get; private set; }
    private bool _tguiReady;

    private void InitializeCivilianTgui()
    {
        var native = ContentsContainer.Children.ToArray();
        var nativeMinimum = MinSize;
        ConsoleHost = new CivilianConsoleHost(() =>
        {
            foreach (var control in native) control.Visible = true;
            MinSize = nativeMinimum;
        });
        foreach (var control in native) control.Visible = false;
        SetSize = new Vector2(780, 560);
        MinSize = new Vector2(520, 400);
        ContentsContainer.AddChild(ConsoleHost);
        ConsoleHost.Panel.SetTguiRestore(() =>
        {
            foreach (var control in native) control.Visible = false;
            ConsoleHost.Visible = true;
            MinSize = new Vector2(520, 400);
            PublishTgui();
        });
        ConsoleHost.Panel.OnClose += Close;
        ConsoleHost.Panel.OnAction += HandleTguiAction;
        PublishTgui();
    }

    private void HandleTguiAction(string action, string payload)
    {
        if (ConsoleHost?.Panel.UsingNative != false || !TguiActionData.TryParse(payload, out var args)) return;
        if (action == "close") { Close(); return; }
        if (action == "server") { OnServerButtonPressed?.Invoke(); return; }
        if (action != "print" || !_tguiReady) return;
        switch (args!.String("id"))
        {
            case "standard" when !PrintButton.Disabled: OnPrintButtonPressed?.Invoke(); break;
            case "rare" when !PrintRareButton.Disabled: OnPrintRareButtonPressed?.Invoke(); break;
        }
    }

    private void PublishTgui()
    {
        if (ConsoleHost == null || ConsoleHost.Panel.UsingNative) return;
        ConsoleHost.Panel.SetIncrementalState("DiskConsole", CivilianDiskConsoleData.Build(Title, TotalLabel.Text,
            ServerButton.Text, _tguiReady, new[] { ("standard", PrintButton), ("rare", PrintRareButton) }), Title ?? "");
    }
}
