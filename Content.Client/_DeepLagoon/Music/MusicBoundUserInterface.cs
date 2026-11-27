using System;
using Content.Client._DeepLagoon.Music;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Music;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.Music;

public sealed partial class MusicBoundUserInterface : BoundUserInterface
{
    private MusicWindow? _menu;
    private MusicTguiWindow? _tgui;
    private bool _classic;
    private float _refresh;
    private float _readyWait;
    private string _query = string.Empty;
    private MusicTrack[] _results = [];
    private MusicSearchStatus _searchStatus = MusicSearchStatus.Ok;
    private bool _searching;
    private MusicState _state = new(MusicPlaybackStatus.Stopped, null, 0f, 0f, 0.75f, false, 0, 0);

    public MusicBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        IoCManager.InjectDependencies(this);
    }

    protected override void Open()
    {
        base.Open();

        if (OpenTgui())
            return;

        if (_menu != null)
        {
            _menu.Open(_menu.Position);
            Push();
            return;
        }

        _menu = this.CreateWindow<MusicWindow>();
        WindowInterfaceStyle.Attach(_menu, "Music", () => false, next =>
        {
            WindowInterfaceStyle.Save("Music", next);
            SwitchInterface(next);
        });
        _menu.OnAction += OnAction;
        Push();
    }

    private void OnAction(string action, string payload)
    {
        var data = ParsePayload(payload);

        switch (action)
        {
            case "close":
                Close();
                break;
            case "focus":
                _tgui?.Panel.Web.FocusTextInput();
                break;
            case "search":
            {
                var query = Arg(data, "query", payload);
                _query = query;

                if (string.IsNullOrWhiteSpace(query))
                {
                    _searching = false;
                    _results = [];
                    _searchStatus = MusicSearchStatus.Ok;
                    Push();
                    break;
                }

                _searching = true;
                SendMessage(new MusicSearchRequestMessage(query));
                break;
            }
            case "play":
                SendMessage(new MusicPlayRequestMessage(Arg(data, "id", payload)));
                break;
            case "pause":
                SendMessage(new MusicControlMessage(MusicControlAction.Pause));
                break;
            case "resume":
                SendMessage(new MusicControlMessage(MusicControlAction.Resume));
                break;
            case "stop":
                SendMessage(new MusicControlMessage(MusicControlAction.Stop));
                break;
            case "next":
                SendMessage(new MusicControlMessage(MusicControlAction.Next));
                break;
            case "seek":
                if (data?.TryFloat("value", out var position) == true)
                    SendMessage(new MusicSetTimeMessage(position));
                break;
            case "gain":
            {
                var value = Arg(data, "value", payload);
                if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var gain))
                    SendMessage(new MusicSetGainMessage(gain));
                break;
            }
        }
    }

    private static TguiActionData? ParsePayload(string payload)
    {
        if (payload.Length == 0 || payload[0] != '{')
            return null;

        return TguiActionData.TryParse(payload, out var data, MusicConstants.MaxMessageBytes) ? data : null;
    }

    private static string Arg(TguiActionData? data, string key, string fallback) =>
        data?.String(key) ?? fallback;

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not MusicState music)
            return;

        _state = music;
        Push();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is not MusicSearchResultMessage results)
            return;

        if (!string.Equals(results.Query, _query, StringComparison.Ordinal))
            return;

        _results = results.Tracks;
        _searchStatus = results.Status;
        _searching = false;
        Push();
    }

    private void Push()
    {
        var statusText = MusicTguiData.Status(_searchStatus);
        if (statusText.Length > 0)
            statusText = Loc.GetString("dl-music-" + statusText);

        if (_tgui != null)
        {
            var current = _state.Current;
            var data = MusicTguiData.Build(_query, _results, _searching, _searchStatus, current, _state.Status,
                _state.Position, _state.Length, _state.Gain, _state.Global, _state.Ready, 1f);
            _tgui.Panel.SetIncrementalState("Music", data, Loc.GetString("dl-music-title"));
            return;
        }

        _menu?.SetResults(_results, statusText);
        _menu?.SetState(MusicTguiData.State(_state.Status), _state.Current, _state.Position, _state.Length, _state.Gain,
            _state.Global, _state.Ready);
    }

    private void SwitchInterface(bool tgui)
    {
        if (!IsOpened)
            return;

        CloseTgui();
        if (_menu != null)
            WindowInterfaceStyle.HideForSwitch(this, _menu);

        _classic = !tgui;
        _readyWait = 0;
        Open();
    }

    private bool OpenTgui()
    {
        if (_classic || !WindowInterfaceStyle.UseTgui("Music"))
            return false;

        _readyWait = _refresh = 0;
        _tgui = new MusicTguiWindow();
        _tgui.OnClose += Close;
        WindowInterfaceStyle.OpenPresentation(this, _tgui, _menu);
        _tgui.Panel.OnClose += Close;
        _tgui.Panel.OnAction += OnAction;
        _tgui.Frame += OnFrame;
        _tgui.Panel.Web.Failed += DeferClassic;
        _tgui.Panel.BindStyle(_tgui, "Music", () => true, SwitchInterface);
        _tgui.Panel.SetNativeFallback(DeferClassic);
        _tgui.Panel.SetStaticData(new TguiData().Object("labels", MusicTguiData.Labels()).ToString());
        Push();
        return true;
    }

    private void DeferClassic() => IoCManager.Resolve<Robust.Client.UserInterface.IUserInterfaceManager>().DeferAction(() =>
    {
        if (_tgui == null || !IsOpened)
            return;

        _classic = true;
        CloseTgui();
        Open();
    });

    private void OnFrame(float seconds)
    {
        if (_tgui == null)
            return;

        if (!_tgui.Panel.Web.IsReady && (_readyWait += seconds) > 8f)
        {
            DeferClassic();
            return;
        }

        if ((_refresh += seconds) < 0.5f)
            return;

        _refresh = 0;
        Push();
    }

    private void CloseTgui()
    {
        var window = _tgui;
        if (window == null)
            return;

        _tgui = null;
        window.Frame -= OnFrame;
        window.Panel.OnClose -= Close;
        window.Panel.OnAction -= OnAction;
        window.Panel.Web.Failed -= DeferClassic;
        WindowInterfaceStyle.ClosePresentation(this, window);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseTgui();

        base.Dispose(disposing);
    }
}
