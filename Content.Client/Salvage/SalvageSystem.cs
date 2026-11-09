using Content.Client.Audio;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Robust.Client.Player;
using Robust.Shared.GameStates;
using Robust.Shared.Timing;

namespace Content.Client.Salvage;

public sealed partial class SalvageSystem : SharedSalvageSystem
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private ContentAudioSystem _audio = default!;
    [Dependency] private IGameTiming _timing = default!;

    private TimeSpan _rewardNoticeUntil;
    private string? _rewardNotice;

    public string? ExpeditionRewardNotice => _timing.RealTime < _rewardNoticeUntil ? _rewardNotice : null;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayAmbientMusicEvent>(OnPlayAmbientMusic);
        SubscribeLocalEvent<SalvageExpeditionComponent, ComponentHandleState>(OnExpeditionHandleState);
        SubscribeNetworkEvent<SalvageExpeditionRewardIssuedEvent>(OnExpeditionRewardIssued);
    }

    private void OnExpeditionHandleState(EntityUid uid, SalvageExpeditionComponent component, ref ComponentHandleState args)
    {
        if (args.Current is not SalvageExpeditionComponentState state)
            return;

        component.Stage = state.Stage;
        component.EndTime = state.EndTime;
        component.ObjectivesTotal = state.ObjectivesTotal;
        component.ObjectivesCompleted = state.ObjectivesCompleted;
        component.RewardAmount = state.RewardAmount;
        component.DispatchedFromOutpost = state.DispatchedFromOutpost;

        if (component.Stage >= ExpeditionStage.MusicCountdown)
        {
            _audio.DisableAmbientMusic();
        }
    }

    private void OnExpeditionRewardIssued(SalvageExpeditionRewardIssuedEvent ev)
    {
        _rewardNotice = Loc.GetString(ev.DispatchedFromOutpost
            ? "salvage-expedition-reward-issued-outpost"
            : "salvage-expedition-reward-issued-cash", ("amount", ev.Amount));
        _rewardNoticeUntil = _timing.RealTime + TimeSpan.FromSeconds(15);
    }

    private void OnPlayAmbientMusic(ref PlayAmbientMusicEvent ev)
    {
        if (ev.Cancelled)
            return;

        var player = _playerManager.LocalEntity;

        if (!TryComp(player, out TransformComponent? xform) ||
            !TryComp<SalvageExpeditionComponent>(xform.MapUid, out var expedition) ||
            expedition.Stage < ExpeditionStage.MusicCountdown)
        {
            return;
        }

        ev.Cancelled = true;
    }
}
