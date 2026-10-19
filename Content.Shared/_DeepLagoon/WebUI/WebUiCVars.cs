using Robust.Shared.Configuration;

namespace Content.Shared._DeepLagoon.WebUI;

[CVarDefs]
public sealed class WebUiCVars
{
    // 0 is deliberately distinct from Classic: only an explicit choice dismisses onboarding.
    public static readonly CVarDef<int> InterfaceStyle = CVarDef.Create("ui.interface_style", 0, CVar.CLIENTONLY | CVar.ARCHIVE);
    // Explicit choices by interface name; missing entries inherit the global default.
    public static readonly CVarDef<string> WindowStyles = CVarDef.Create("ui.window_styles", "", CVar.CLIENTONLY | CVar.ARCHIVE);
    public static bool UseTgui(int style) => style == 1;
    public static bool HasChosenStyle(int style) => style is 1 or 2;
    // Opt-in for a development session; not archived and not sent by the server.
    public static readonly CVarDef<string> DevServer = CVarDef.Create("tgui.dev_server", "", CVar.CLIENTONLY);
    public static readonly CVarDef<float> ChatPanelWidth = CVarDef.Create("ui.chat_panel_width", 0.2f, CVar.CLIENTONLY | CVar.ARCHIVE);
    public static readonly CVarDef<bool> VanillaChat = CVarDef.Create("ui.vanilla_chat", true, CVar.CLIENTONLY | CVar.ARCHIVE);
}
