using Robust.Shared.Configuration;
using Content.Server.Voting.Managers;
using Content.Shared.GameTicking;
using Content.Shared.Voting;
using Content.Shared.CCVar;
using Robust.Server.Player;
using Content.Server.GameTicking;
using System.Linq;
using Content.Server.Voting;

namespace Content.Server.AutoVote;

public sealed partial class AutoVoteSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] public IVoteManager _voteManager = default!;
    [Dependency] public IPlayerManager _playerManager = default!;

    public bool _shouldVoteNextJoin = false;
    private readonly List<IVoteHandle> _automaticVotes = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnReturnedToLobby);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerJoinedLobby);
    }

    public void OnReturnedToLobby(RoundRestartCleanupEvent ev) => CallAutovote();

    public void OnPlayerJoinedLobby(PlayerJoinedLobbyEvent ev)
    {
        if (!_shouldVoteNextJoin)
            return;

        CallAutovote();
    }

    private void CallAutovote()
    {
        // A lobby restart can occur while the previous automatic vote is still open.
        // Retire only votes created by this system before starting the new lobby's votes.
        foreach (var vote in _automaticVotes)
        {
            if (!vote.Finished)
                vote.Cancel();
        }
        _automaticVotes.Clear();
        _shouldVoteNextJoin = false;

        if (!_cfg.GetCVar(CCVars.AutoVoteEnabled))
            return;

        if (_playerManager.PlayerCount == 0)
        {
            _shouldVoteNextJoin = true;
            return;
        }

        var existing = _voteManager.ActiveVotes.Select(vote => vote.Id).ToHashSet();
        if (_cfg.GetCVar(CCVars.MapAutoVoteEnabled))
            _voteManager.CreateStandardVote(null, StandardVoteType.Map);
        if (_cfg.GetCVar(CCVars.PresetAutoVoteEnabled))
            _voteManager.CreateStandardVote(null, StandardVoteType.Preset);
        _automaticVotes.AddRange(_voteManager.ActiveVotes.Where(vote => !existing.Contains(vote.Id)));
    }
}
