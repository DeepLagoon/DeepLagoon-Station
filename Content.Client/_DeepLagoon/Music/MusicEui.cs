using Content.Client.Eui;
using Content.Shared._DeepLagoon.Music;
using Content.Shared.Eui;
using Robust.Shared.IoC;

namespace Content.Client._DeepLagoon.Music;

public sealed class MusicEui : BaseEui
{
    private MusicWindow? _window;

    public override void Opened()
    {
        _window = new MusicWindow();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.OnAction += OnAction;
        _window.OpenCentered();
    }

    public override void Closed()
    {
        if (_window == null)
            return;

        _window.OnClose -= () => SendMessage(new CloseEuiMessage());
        _window.OnAction -= OnAction;
        _window.Close();
        _window.Dispose();
        _window = null;
    }

    private void OnAction(string action, string payload)
    {
        switch (action)
        {
            case "close":
                SendMessage(new CloseEuiMessage());
                break;
            case "search":
                SendMessage(new MusicEuiSearchMsg(payload));
                break;
            case "play":
                SendMessage(new MusicEuiPlayMsg(payload));
                break;
            case "pause":
                SendMessage(new MusicEuiControlMsg(MusicControlAction.Pause));
                break;
            case "resume":
                SendMessage(new MusicEuiControlMsg(MusicControlAction.Resume));
                break;
            case "stop":
                SendMessage(new MusicEuiControlMsg(MusicControlAction.Stop));
                break;
            case "next":
                SendMessage(new MusicEuiControlMsg(MusicControlAction.Next));
                break;
            case "gain":
                if (float.TryParse(payload, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var gain))
                    SendMessage(new MusicEuiGainMsg(gain));
                break;
        }
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not MusicEuiState music)
            return;

        if (!music.Enabled)
        {
            _window?.SetResults([], Loc.GetString("dl-music-not-configured"));
            _window?.SetState("stopped", null, 0f, 0f, music.Gain, false, 0);
            return;
        }

        var status = music.Status switch
        {
            "playing" => "playing",
            "paused" => "paused",
            "downloading" => "loading",
            _ => "stopped",
        };

        _window?.SetState(status, music.Current, music.Position, music.Length, music.Gain, true, music.Listeners);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is not MusicEuiResultsMsg results)
            return;

        var key = MusicTguiData.Status(results.Status);
        var text = key.Length > 0 ? Loc.GetString("dl-music-" + key) : string.Empty;
        _window?.SetResults(results.Tracks, text);
    }
}
