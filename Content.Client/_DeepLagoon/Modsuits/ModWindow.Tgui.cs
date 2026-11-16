using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Modsuits;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Modsuits;

public sealed partial class ModWindow
{
    private const string InterfaceName = "ModsuitPanel";
    private TguiPanel _tgui = default!;
    private float _webWait;

    private void InitializeTgui(ScrollContainer native)
    {
        _tgui = new TguiPanel(inheritChatAppearance: false, captureKeyboard: true, showWindowControls: false);
        _tgui.OnAction += OnTguiAction;
        _tgui.OnClose += Close;
        _tgui.SetNativeFallback(() => native.Visible = true);
        _tgui.SetTguiRestore(() =>
        {
            _webWait = 0;
            native.Visible = false;
        });
        native.Visible = !WindowInterfaceStyle.UseTgui(InterfaceName);
        _tgui.SetStaticData(new TguiData().Object("labels", TguiLabels()).ToString());
        ContentsContainer.AddChild(_tgui);
        _tgui.SetState(InterfaceName, "{}", Title ?? "");
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!_tgui.UsingNative && !_tgui.Web.IsReady && (_webWait += args.DeltaSeconds) >= 8)
            _tgui.SwitchToNative();
    }

    private void OnTguiAction(string action, string payload)
    {
        if (_tgui.UsingNative || !Enum.TryParse<ModOperation>(action, out var operation) ||
            !Enum.IsDefined(operation) || !TguiActionData.TryParse(payload, out var data) || data == null)
            return;

        var target = data.TryInt("target", out var id) ? new NetEntity(id) : default;
        Send(operation, target, data.String("value") ?? "");
    }

    private void PublishTgui(ModUiState state)
    {
        var label = string.IsNullOrWhiteSpace(state.Label) ? state.Number : state.Label;
        var callStatus = state.Call switch
        {
            ModCallState.Dialing => Loc.GetString("mod-link-dialing", ("peer", state.Peer)),
            ModCallState.Incoming => Loc.GetString("mod-link-incoming", ("peer", state.Peer)),
            ModCallState.Connected => Loc.GetString("mod-link-connected", ("peer", state.Peer)),
            _ => Loc.GetString(state.Contacts.Length == 0 ? "mod-link-empty" : "mod-link-available"),
        };
        var data = new TguiData()
            .Bool("ready", true)
            .Bool("control", state.Control)
            .Bool("active", state.Active)
            .Bool("busy", state.Busy)
            .Bool("service", state.Service)
            .Bool("wearable", state.WearableControls)
            .Number("charge", state.Charge)
            .Number("capacity", state.Capacity)
            .Number("complexity", state.Complexity)
            .String("name", state.Details.Name)
            .String("wearer", state.Details.Wearer)
            .String("cell", state.Details.Cell)
            .String("selected", state.Details.SelectedModule)
            .String("info", state.Details.BodyTemperature is {} temperature
                ? Loc.GetString("mod-panel-body-temperature", ("temperature", temperature)) : "")
            .String("linkName", label)
            .String("linkNumber", state.Number)
            .Number("frequency", state.Frequency)
            .Bool("canSetFrequency", state.CanSetFrequency)
            .String("call", state.Call.ToString())
            .String("callStatus", callStatus)
            .Array("contacts", state.Contacts.Select(contact => new TguiData()
                .Number("id", contact.Id.Id).String("name", contact.Name)))
            .Array("parts", state.Parts.Select(part => new TguiData().String("slot", part.Slot)
                .String("name", part.Name).Bool("deployed", part.Deployed)))
            .Array("modules", state.Modules.Select(module => new TguiData()
                .Number("id", module.Id.Id)
                .String("name", module.Name)
                .String("description", module.Description)
                .Number("complexity", module.Complexity)
                .Bool("active", module.Active)
                .Bool("usable", module.Usable)
                .Bool("requiresPower", module.RequiresPower)
                .Bool("pinned", module.Pinned)
                .Number("useCharge", module.UseCharge)
                .String("cooldown", ModuleCooldown(module))
                .Bool("configurable", module.TargetTemperature != null)
                .Number("temperature", module.TargetTemperature ?? 0)));
        _tgui.SetIncrementalState(InterfaceName, data, Title ?? "");
    }

    private static TguiData TguiLabels() => new TguiData()
        .String("parameters", Loc.GetString("mod-section-parameters"))
        .String("hardware", Loc.GetString("mod-section-hardware"))
        .String("parts", Loc.GetString("mod-section-parts"))
        .String("cell", Loc.GetString("mod-section-cell"))
        .String("info", Loc.GetString("mod-section-info"))
        .String("modules", Loc.GetString("mod-section-modules"))
        .String("status", Loc.GetString("mod-panel-label-status"))
        .String("busy", Loc.GetString("mod-panel-label-busy"))
        .String("active", Loc.GetString("mod-panel-label-active"))
        .String("idle", Loc.GetString("mod-panel-label-idle"))
        .String("activate", Loc.GetString("mod-suit-activate"))
        .String("deactivate", Loc.GetString("mod-suit-deactivate"))
        .String("cover", Loc.GetString("mod-panel-label-cover"))
        .String("open", Loc.GetString("mod-panel-label-open"))
        .String("closed", Loc.GetString("mod-panel-label-closed"))
        .String("selected", Loc.GetString("mod-panel-label-selected"))
        .String("complexity", Loc.GetString("mod-panel-label-complexity"))
        .String("wearer", Loc.GetString("mod-panel-label-wearer"))
        .String("control", Loc.GetString("mod-panel-label-control"))
        .String("head", Loc.GetString("mod-panel-part-head"))
        .String("outerClothing", Loc.GetString("mod-panel-part-outerclothing"))
        .String("gloves", Loc.GetString("mod-panel-part-gloves"))
        .String("shoes", Loc.GetString("mod-panel-part-shoes"))
        .String("deployed", Loc.GetString("mod-panel-label-deployed"))
        .String("stowed", Loc.GetString("mod-panel-label-stowed"))
        .String("cellType", Loc.GetString("mod-panel-label-cell-type"))
        .String("cellCharge", Loc.GetString("mod-panel-label-cell-charge"))
        .String("empty", Loc.GetString("mod-power-empty"))
        .String("none", Loc.GetString("mod-panel-none"))
        .String("noInfo", Loc.GetString("mod-panel-no-info"))
        .String("noModules", Loc.GetString("mod-panel-no-modules"))
        .String("idleDraw", Loc.GetString("mod-panel-idle-draw"))
        .String("activeDraw", Loc.GetString("mod-panel-active-draw"))
        .String("useDraw", Loc.GetString("mod-panel-use-draw"))
        .String("cooldown", Loc.GetString("mod-panel-cooldown-short"))
        .String("actions", Loc.GetString("mod-panel-label-actions"))
        .String("use", Loc.GetString("mod-module-use"))
        .String("stop", Loc.GetString("mod-module-stop"))
        .String("pin", Loc.GetString("mod-panel-pin"))
        .String("pinned", Loc.GetString("mod-panel-pinned"))
        .String("configure", Loc.GetString("mod-panel-label-configure"))
        .String("remove", Loc.GetString("mod-module-remove"))
        .String("temperature", Loc.GetString("mod-module-temperature"))
        .String("applyTemperature", Loc.GetString("mod-module-set-temperature"))
        .String("hardlight", Loc.GetString("mod-hardlight-title"))
        .String("rename", Loc.GetString("mod-link-rename"))
        .String("tune", Loc.GetString("mod-link-tune"))
        .String("call", Loc.GetString("mod-link-call"))
        .String("accept", Loc.GetString("mod-link-accept"))
        .String("end", Loc.GetString("mod-link-end"))
        .String("decline", Loc.GetString("mod-link-decline"))
        .String("frequency", Loc.GetString("mod-panel-label-frequency"));
}
