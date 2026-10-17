using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;

namespace Content.Client._Mono.Research.UI;

public sealed partial class PointDiskConsoleMenu
{
    public CivilianConsoleHost? ConsoleHost { get; private set; }
    private bool _tguiReady;

    private void InitializeCivilianTgui()
    {
        if (!TguiPanel.Enabled) return;
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
            case "1k" when !Print1KButton.Disabled: OnPrint1KButtonPressed?.Invoke(); break;
            case "5k" when !Print5KButton.Disabled: OnPrint5KButtonPressed?.Invoke(); break;
            case "10k" when !Print10KButton.Disabled: OnPrint10KButtonPressed?.Invoke(); break;
        }
    }

    private void PublishTgui()
    {
        if (ConsoleHost == null || ConsoleHost.Panel.UsingNative) return;
        ConsoleHost.Panel.SetIncrementalState("DiskConsole", CivilianDiskConsoleData.Build(Title, TotalLabel.Text,
            ServerButton.Text, _tguiReady, new[] { ("1k", Print1KButton), ("5k", Print5KButton), ("10k", Print10KButton) }), Title ?? "");
    }
}
