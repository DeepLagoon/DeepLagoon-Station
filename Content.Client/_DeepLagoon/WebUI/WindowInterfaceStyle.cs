using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Archived overrides use stable interface names, never translated titles or entity ids.</summary>
public static class WindowInterfaceStyle
{
    private static readonly Dictionary<(EntityUid Owner, Enum Key), Vector2> PresentationPositions = new();

    /// <summary>Hide the registered native window without closing or disposing its BUI.</summary>
    public static void HideForSwitch(BoundUserInterface bui, BaseWindow window)
    {
        window.OnClose -= bui.Close;
        window.Close();
        window.OnClose += bui.Close;
    }

    /// <summary>TGUI is disposed separately; only the reusable native window is registered with the engine.</summary>
    public static void OpenPresentation(BoundUserInterface bui, BaseWindow window, BaseWindow? native)
    {
        if (native != null)
            window.Open(native.Position);
        else if (PresentationPositions.TryGetValue((bui.Owner, bui.UiKey), out var saved))
            window.Open(saved);
        else if (IoCManager.Resolve<IEntityManager>().System<UserInterfaceSystem>().TryGetPosition(bui.Owner, bui.UiKey, out var position))
            window.Open(position);
        else
            window.OpenCentered();
    }

    /// <summary>Keep TGUI position across BUI lifetimes without registering a second engine control.</summary>
    public static void ClosePresentation(BoundUserInterface bui, BaseWindow window)
    {
        if (PresentationPositions.Count > 256)
            PresentationPositions.Clear();
        PresentationPositions[(bui.Owner, bui.UiKey)] = window.Position;
        window.OnClose -= bui.Close;
        window.Close();
    }

    public static bool UseTgui(string key)
    {
        var cfg = IoCManager.Resolve<IConfigurationManager>();
        return Resolve(cfg.GetCVar(WebUiCVars.WindowStyles), key, cfg.GetCVar(WebUiCVars.InterfaceStyle));
    }

    public static bool Resolve(string saved, string key, int global)
    {
        foreach (var entry in saved.Split(';'))
        {
            if (entry == key + "=1") return true;
            if (entry == key + "=2") return false;
        }
        return WebUiCVars.UseTgui(global);
    }

    public static string WithChoice(string saved, string key, bool tgui) =>
        string.Join(";", saved.Split(';').Where(entry => entry.Length > 0 && !entry.StartsWith(key + "=", StringComparison.Ordinal))
            .Append(key + (tgui ? "=1" : "=2")));

    public static void Save(string key, bool tgui)
    {
        var cfg = IoCManager.Resolve<IConfigurationManager>();
        cfg.SetCVar(WebUiCVars.WindowStyles, WithChoice(cfg.GetCVar(WebUiCVars.WindowStyles), key, tgui));
        cfg.SaveToFile();
    }

    /// <summary>The caller owns the button and updates it if the same surface changes mode.</summary>
    public static Button Attach(Control owner, string key, Func<bool> current, Action<bool> change)
    {
        var button = new Button { Text = current() ? "SS14" : "TGUI", ToolTip = Loc.GetString("dl-ui-window-switch"), MinSize = new(46, 22) };
        button.OnPressed += _ => owner.UserInterfaceManager.DeferAction(() =>
        {
            if (owner.Disposed) return;
            var next = !current();
            change(next);
            if (!button.Disposed) button.Text = current() ? "SS14" : "TGUI";
        });
        if (owner is FancyWindow or DefaultWindow)
        {
            var close = owner.FindControl<TextureButton>("CloseButton");
            var index = close.GetPositionInParent();
            close.Parent!.AddChild(button);
            button.SetPositionInParent(index);
        }
        else
        {
            button.HorizontalAlignment = Control.HAlignment.Right;
            button.VerticalAlignment = Control.VAlignment.Top;
            owner.AddChild(button);
        }
        return button;
    }
}
