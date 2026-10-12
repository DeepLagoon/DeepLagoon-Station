using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Lathe;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Research.Components;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client.Lathe.UI;

/// <summary>
/// DeepLagoon: the TGUI menu of every lathe. It uses the lathe's existing state and messages (the server only
/// adds the progress of the current print), and the native <see cref="LatheMenu"/> stays as the fallback:
/// when the page does not come up (no CEF, a broken bundle).
/// </summary>
public sealed partial class LatheBoundUserInterface
{
    private const string InterfaceName = "LatheConsole";
    private const int MaxIconRequest = 128;
    private const int MaxQuantity = 999;

    // If the page has not reported in by then (no CEF, a broken bundle), the native menu opens instead.
    private const float ReadyTimeoutSeconds = 8f;

    // The engine saves positions only for windows it created; the native menu keeps that, this is for ours.
    private static readonly Dictionary<EntityUid, Vector2> SavedPositions = new();

    private LatheTguiWindow? _tguiWindow;
    private LatheTguiData? _tguiData;
    private bool _classic;
    private static bool _classicForSession;
    private bool _tguiDirty;
    private bool _awaitingReady;
    private float _readyWaited;
    private float _liveTimer;
    private string _liveSignature = string.Empty;
    private string _title = string.Empty;
    private string _staticKey = string.Empty;
    private string _staticJson = string.Empty;
    private HashSet<string> _knownRecipes = new();

    private bool OpenTgui()
    {
        if (_classic || _classicForSession)
            return false;

        _tguiData = new LatheTguiData(Owner, EntMan);
        _title = EntMan.GetComponent<MetaDataComponent>(Owner).EntityName;

        var window = new LatheTguiWindow { Title = _title };
        _tguiWindow = window;
        window.OnClose += Close;
        window.Frame += OnTguiFrame;
        window.Panel.OnClose += Close;
        window.Panel.OnAction += OnTguiAction;
        // The panel publishes its own state first on every (re)load; static data follows it.
        window.Panel.Web.Ready += SendTguiStatic;
        window.Panel.Web.Failed += DeferSwitchToClassic;

        if (SavedPositions.TryGetValue(Owner, out var position))
            window.Open(position);
        else
            window.OpenCenteredRight();

        window.Panel.SetState(InterfaceName, "{}", _title);
        _awaitingReady = true;
        _readyWaited = 0;
        PushTguiState();
        return true;
    }

    private bool UpdateTgui(BoundUserInterfaceState state)
    {
        if (_tguiWindow == null)
            return false;

        PushTguiState();
        return true;
    }

    public override void OnProtoReload(PrototypesReloadedEventArgs args)
    {
        base.OnProtoReload(args);

        if (_tguiWindow == null || !args.WasModified<Content.Shared.Research.Prototypes.LatheRecipePrototype>())
            return;

        LatheTguiData.ClearCache();
        TguiRecipeIcons.Reset();
        _staticKey = string.Empty;
        _tguiDirty = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseTguiWindow();

        base.Dispose(disposing);
    }

    private void CloseTguiWindow()
    {
        if (_tguiWindow is not { } window)
            return;

        _tguiWindow = null;
        window.OnClose -= Close;
        window.Frame -= OnTguiFrame;

        if (SavedPositions.Count > 256)
            SavedPositions.Clear();
        SavedPositions[Owner] = window.Position;

        window.Close();
        // GameWebView keeps only the CEF control alive during asynchronous close.
#pragma warning disable CS0618
        if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
    }

    private void SwitchToClassic()
    {
        if (_classic || _tguiWindow == null || !IsOpened)
            return;

        _classic = true;
        _classicForSession = true;
        CloseTguiWindow();
        Open();
        if (State != null)
            UpdateState(State);
    }

    private void DeferSwitchToClassic() =>
        IoCManager.Resolve<IUserInterfaceManager>().DeferAction(SwitchToClassic);

    private void PushTguiState()
    {
        _tguiDirty = false;
        if (_tguiWindow == null || _tguiData == null || State is not LatheUpdateState state ||
            !EntMan.TryGetComponent<LatheComponent>(Owner, out var lathe))
        {
            return;
        }

        var key = _tguiData.StaticKey(state, lathe);
        if (key != _staticKey)
        {
            _staticKey = key;
            _knownRecipes = _tguiData.RecipeIds(state);
            _staticJson = _tguiData.BuildStatic(state, lathe, key);
            SendTguiStatic();
        }

        _liveSignature = _tguiData.LiveSignature(lathe);
        _tguiWindow.Panel.SetState(InterfaceName, _tguiData.BuildDynamic(state, lathe), _title);
    }

    private void SendTguiStatic()
    {
        if (_tguiWindow is { Panel.Web.IsReady: true } && _staticJson.Length > 0)
            _tguiWindow.Panel.Web.Send("update", _staticJson);
    }

    private void OnTguiFrame(float seconds)
    {
        if (_tguiWindow == null)
            return;

        if (_awaitingReady)
        {
            if (_tguiWindow.Panel.Web.IsReady)
            {
                _awaitingReady = false;
            }
            else
            {
                _readyWaited += seconds;
                if (_readyWaited > ReadyTimeoutSeconds)
                {
                    _awaitingReady = false;
                    DeferSwitchToClassic();
                    return;
                }
            }
        }

        // The machine's stock, multipliers, storage and beaker are replicated on their own, not with the menu
        // state; a few looks a second keep the menu in step with them.
        if (EntMan.TryGetComponent<LatheComponent>(Owner, out var lathe) && _tguiData != null)
        {
            _liveTimer += seconds;
            if (_liveTimer >= 0.25f)
            {
                _liveTimer = 0;
                if (_tguiData.LiveSignature(lathe) != _liveSignature)
                    _tguiDirty = true;
            }
        }

        if (_tguiDirty)
            PushTguiState();
    }

    private void OnTguiAction(string action, string payload)
    {
        switch (action)
        {
            case "servers":
                SendMessage(new ConsoleServerSelectionMessage());
                return;
        }

        if (!TguiActionData.TryParse(payload, out var data) || data == null)
            return;

        // The browser holds the keyboard while it has the focus, so after a click on a button the walking keys
        // would go to the page. A click that does something releases it; typing in a field is not an action.
        if (action is "queue" or "cancel" or "loop" or "skip" or "eject")
            ReleaseKeyboard();

        switch (action)
        {
            case "queue":
                if (data.String("id") is { } id && _knownRecipes.Contains(id) && data.TryInt("qty", out var quantity))
                    SendMessage(new LatheQueueRecipeMessage(id, Math.Clamp(quantity, 1, MaxQuantity)));
                break;
            case "cancel":
                if (data.TryInt("index", out var index) &&
                    State is LatheUpdateState { Queue: var queue } && queue.Any(batch => batch.Index == index))
                {
                    SendMessage(new LatheRecipeCancelMessage(index));
                }
                break;
            case "loop":
                SendMessage(new LatheSetLoopingMessage(data.String("value") == "true"));
                break;
            case "skip":
                SendMessage(new LatheSetSkipMessage(data.String("value") == "true"));
                break;
            case "eject":
                // Arrays are not used for the check: in C# 14 their Contains binds to MemoryExtensions, which the sandbox refuses.
                if (data.String("id") is { } material && data.TryInt("sheets", out var sheets) &&
                    sheets is 1 or 5 or 10 or 30)
                {
                    EntMan.RaisePredictiveEvent(new EjectMaterialMessage(EntMan.GetNetEntity(Owner), material, sheets));
                }
                break;
            case "icons":
                SendTguiIcons(data.String("ids"));
                break;
        }
    }

    private void ReleaseKeyboard()
    {
        if (_tguiWindow is not { } window)
            return;

        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        for (var focused = ui.KeyboardFocused; focused != null; focused = focused.Parent)
        {
            if (focused != window.Panel)
                continue;
            ui.KeyboardFocused?.ReleaseKeyboardFocus();
            return;
        }
    }

    private void SendTguiIcons(string? ids)
    {
        if (_tguiWindow == null || _tguiData == null || ids == null)
            return;

        var queued = State is LatheUpdateState state
            ? state.Queue.Select(batch => batch.Recipe.ID).ToHashSet()
            : new HashSet<string>();
        var requested = ids.Split(',')
            .Where(id => _knownRecipes.Contains(id) || queued.Contains(id))
            .Distinct()
            .Take(MaxIconRequest)
            .ToList();
        if (requested.Count > 0)
            _tguiWindow.Panel.Web.Send("update", _tguiData.BuildIcons(requested));
    }
}
