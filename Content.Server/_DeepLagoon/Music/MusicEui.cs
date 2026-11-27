using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._DeepLagoon.Music;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Music;

public sealed class MusicEui : BaseEui
{
    [Dependency] private readonly IAdminManager _admins = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly MusicSystem _music;
    private readonly List<MusicTrack> _results = new();
    private MusicSearchStatus _searchStatus = MusicSearchStatus.Ok;
    private string _query = string.Empty;
    private bool _busy;
    private TimeSpan _lastSearch;

    public MusicEui()
    {
        IoCManager.InjectDependencies(this);
        _music = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<MusicSystem>();
    }

    public bool Authorized => !IsShutDown && _admins.HasAdminFlag(Player, AdminFlags.Admin);

    public override void Opened()
    {
        _music.StateChanged += OnMusicChanged;
        StateDirty();
    }

    public override void Closed()
    {
        _music.StateChanged -= OnMusicChanged;
    }

    private void OnMusicChanged() => StateDirty();

    public override EuiStateBase GetNewState()
    {
        if (!Authorized)
            return new MusicEuiState("stopped", null, 0f, 0f, 0f, false, 0, 0);

        return _music.BuildEuiState();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!Authorized)
        {
            Close();
            return;
        }

        switch (msg)
        {
            case MusicEuiSearchMsg search:
                HandleSearch(search);
                break;

            case MusicEuiResultsMsg:
                break;

            case MusicEuiPlayMsg play:
                if (MusicRules.ValidTrackId(play.TrackId, out var id))
                    _ = _music.PlayGlobalAsync(id, string.Empty, string.Empty);
                break;

            case MusicEuiControlMsg control:
                switch (control.Action)
                {
                    case MusicControlAction.Pause: _music.Pause(); break;
                    case MusicControlAction.Resume: _music.Resume(); break;
                    case MusicControlAction.Stop: _music.Stop(); break;
                    case MusicControlAction.Next: _music.Next(); break;
                }

                break;

            case MusicEuiGainMsg gain:
                _music.SetGain(gain.Gain);
                break;
        }

        StateDirty();
    }

    private void HandleSearch(MusicEuiSearchMsg search)
    {
        if (_busy || _timing.CurTime - _lastSearch < TimeSpan.FromSeconds(1))
            return;

        if (!MusicRules.ValidQuery(search.Query, out var cleaned))
        {
            _results.Clear();
            _searchStatus = MusicSearchStatus.BadQuery;
            SendMessage(new MusicEuiResultsMsg([], _searchStatus));
            return;
        }

        _busy = true;
        _query = cleaned;
        _lastSearch = _timing.CurTime;

        _ = SearchAsync(cleaned);
    }

    private async Task SearchAsync(string query)
    {
        try
        {
            var (tracks, status) = await _music.SearchAsync(query);
            _results.Clear();
            _results.AddRange(tracks);
            _searchStatus = status;
            SendMessage(new MusicEuiResultsMsg(tracks, status));
        }
        finally
        {
            _busy = false;
            StateDirty();
        }
    }
}
