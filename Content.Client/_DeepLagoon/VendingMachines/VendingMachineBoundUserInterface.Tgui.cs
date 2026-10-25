using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.VendingMachines;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._NF.Bank;
using Content.Shared.VendingMachines;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client.VendingMachines;

public sealed partial class VendingMachineBoundUserInterface
{
    private static readonly Dictionary<EntityUid, Vector2> SavedTguiPositions = new();
    private VendingMachineTguiWindow? _tguiWindow;
    private VendingMachineTguiData? _tguiData;
    private string _catalogueKey = "";
    private string _inventoryKey = "";
    private TguiData _stockData = new();
    private static readonly HashSet<string> StockFields = new() { "stock" };
    private string _title = "";
    private bool _classic;
    private bool _hasBankAccount;
    private bool _acceptsCash;
    private float _readyWait;
    private float _refreshWait;

    private bool OpenTgui()
    {
        if (_classic || !WindowInterfaceStyle.UseTgui("VendingMachine"))
            return false;

        _title = EntMan.TryGetComponent<MetaDataComponent>(Owner, out var meta)
            ? meta.EntityName : Loc.GetString("vending-machine-nf-fallback-title");
        _tguiData = new VendingMachineTguiData(EntMan);
        _catalogueKey = "";
        _readyWait = _refreshWait = 0;
        var window = new VendingMachineTguiWindow { Title = _title };
        _tguiWindow = window;
        window.OnClose += Close;
        window.Frame += OnTguiFrame;
        window.Panel.OnClose += Close;
        window.Panel.OnAction += OnTguiAction;
        window.Panel.Web.Failed += DeferClassic;
        window.Panel.BindStyle(window, "VendingMachine", () => true, SwitchInterface);
        window.Panel.SetNativeFallback(DeferClassic);
        if (SavedTguiPositions.TryGetValue(Owner, out var position))
            window.Open(position);
        else
            window.OpenCenteredLeft();
        return true;
    }

    private void OnTguiFrame(float seconds)
    {
        if (_tguiWindow == null)
            return;

        if (!_tguiWindow.Panel.Web.IsReady)
        {
            _readyWait += seconds;
            if (_readyWait >= 8f)
            {
                DeferClassic();
                return;
            }
        }
        else
            _readyWait = 0;

        // Bank balance, market prices and appearance can change without an inventory update.
        _refreshWait += seconds;
        if (_refreshWait < 0.25f)
            return;
        _refreshWait = 0;
        RefreshPaymentState();
        PushTguiState(inventoryChanged: false);
    }

    private void PushTguiState(bool inventoryChanged = true)
    {
        if (_tguiWindow == null || _tguiData == null)
            return;

        if (inventoryChanged)
        {
            _inventoryKey = string.Join(',', _cachedInventory.Select(VendingMachineTguiData.Key));
            _stockData = new TguiData().Array("stock", VendingMachineTguiData.Stock(_cachedInventory));
        }
        var key = _mod.ToString(CultureInfo.InvariantCulture) + ":" + _requiresCash + ":" + _inventoryKey;
        if (key != _catalogueKey)
        {
            _catalogueKey = key;
            _tguiWindow.Panel.SetStaticData(_tguiData.BuildStatic(_cachedInventory, _mod, _requiresCash));
            // SetStaticData resets the incremental baseline, so the next update replays the catalogue too.
        }

        var visual = VendingMachineVisualState.Normal;
        if (EntMan.TryGetComponent<AppearanceComponent>(Owner, out var appearance))
            EntMan.System<SharedAppearanceSystem>().TryGetData(Owner, VendingMachineVisuals.VisualState, out visual, appearance);

        _tguiWindow.Panel.SetIncrementalState("VendingMachine", _stockData.SelectFields(StockFields)
            .String("title", _title)
            .Bool("hasBankAccount", _hasBankAccount).String("balanceText", BankSystemExtensions.ToSpesoString(_balance))
            .Bool("acceptsCash", _acceptsCash).String("cashText", BankSystemExtensions.ToSpesoString(_cashSlotBalance))
            .Bool("requiresCash", _requiresCash).String("status", visual switch
            {
                VendingMachineVisualState.Off => "off",
                VendingMachineVisualState.Broken => "broken",
                VendingMachineVisualState.Eject => "busy",
                VendingMachineVisualState.Deny => "denied",
                _ => "ready",
            }), _title);
    }

    private void OnTguiAction(string action, string payload)
    {
        if (_tguiWindow == null || !TguiActionData.TryParse(payload, out var data) || data == null)
            return;

        // Re-read the authorized inventory: an emag/contraband switch can invalidate an open catalogue.
        _cachedInventory = EntMan.System<VendingMachineSystem>().GetAllInventory(Owner);
        if (action == "vend" && data.String("id") is { } id && data.TryInt("type", out var type))
        {
            var entry = _cachedInventory.FirstOrDefault(entry => entry.ID == id && (int) entry.Type == type && entry.Amount > 0);
            if (entry == null)
                return;

            ReleaseTguiKeyboard();
            SendMessage(new VendingMachineEjectMessage(entry.Type, entry.ID));
        }
        else if (action == "icons" && _tguiData != null && data.String("ids") is { } ids)
        {
            var allowed = _cachedInventory.Select(entry => entry.ID).ToHashSet();
            var requested = ids.Split(',').Where(allowed.Contains).Distinct().Take(100).ToList();
            if (requested.Count > 0)
                _tguiWindow.Panel.Web.Send("update", _tguiData.BuildIcons(requested));
        }
    }

    private void ReleaseTguiKeyboard()
    {
        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        for (var focused = ui.KeyboardFocused; focused != null; focused = focused.Parent)
        {
            if (focused != _tguiWindow?.Panel)
                continue;
            ui.KeyboardFocused?.ReleaseKeyboardFocus();
            return;
        }
    }

    public override void OnProtoReload(PrototypesReloadedEventArgs args)
    {
        base.OnProtoReload(args);
        _catalogueKey = "";
        _tguiData?.Reset();
        Update();
    }

    private void DeferClassic() => IoCManager.Resolve<IUserInterfaceManager>().DeferAction(SwitchToClassic);

    private void SwitchToClassic()
    {
        if (_tguiWindow == null || !IsOpened)
            return;
        _classic = true;
        CloseTguiWindow();
        Open();
    }

    private void CloseTguiWindow()
    {
        if (_tguiWindow is not { } window)
            return;
        _tguiWindow = null;
        window.OnClose -= Close;
        window.Frame -= OnTguiFrame;
        window.Panel.OnClose -= Close;
        window.Panel.OnAction -= OnTguiAction;
        window.Panel.Web.Failed -= DeferClassic;
        if (SavedTguiPositions.Count > 256)
            SavedTguiPositions.Clear();
        SavedTguiPositions[Owner] = window.Position;
        window.Close();
#pragma warning disable CS0618
        if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
    }

    private void SwitchInterface(bool tgui)
    {
        if (!IsOpened) return;
        CloseTguiWindow();
        if (_menu is { } native)
            WindowInterfaceStyle.HideForSwitch(this, native);
        _classic = !tgui;

        Open();
    }
}
