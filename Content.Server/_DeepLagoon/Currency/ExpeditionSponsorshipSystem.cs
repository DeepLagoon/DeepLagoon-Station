using Content.Server._NF.Bank;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Currency;

public sealed partial class ExpeditionSponsorshipSystem : EntitySystem
{
    private const int Payout = 5_000;
    private const double IntervalSeconds = 60 * 60;

    [Dependency] private BankSystem _bank = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    private readonly Dictionary<NetUserId, Sponsorship> _sponsorships = new();

    private sealed class Sponsorship(EntityUid mind, int slot)
    {
        public readonly EntityUid Mind = mind;
        public readonly int Slot = slot;
        public double Seconds;
    }

    public override void Initialize()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawn);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _sponsorships.Clear());
    }

    private void OnSpawn(PlayerSpawnCompleteEvent ev)
    {
        var user = ev.Player.UserId;
        if (ev.JobId != "Contractor" || !_minds.TryGetMind(ev.Mob, out var mind, out _) ||
            !_preferences.TryGetCachedPreferences(user, out var preferences) ||
            !preferences.TryIndexOfCharacter(ev.Profile, out var slot))
        {
            _sponsorships.Remove(user);
            return;
        }

        if (!_sponsorships.TryGetValue(user, out var current) || current.Mind != mind || current.Slot != slot)
            _sponsorships[user] = new Sponsorship(mind, slot);
    }

    public override void Update(float frameTime)
    {
        if (_ticker.RunLevel != GameRunLevel.InRound)
            return;

        foreach (var (user, sponsorship) in _sponsorships)
        {
            if (!_players.TryGetSessionById(user, out var session) || session.Status != SessionStatus.InGame ||
                session.AttachedEntity is not { } mob || !TryComp<MindComponent>(sponsorship.Mind, out var mind) ||
                mind.OwnedEntity != mob || !TryComp<MobStateComponent>(mob, out var state) ||
                state.CurrentState is not (MobState.Alive or MobState.Critical) || HasComp<IronmanComponent>(mob))
                continue;

            sponsorship.Seconds += frameTime;
            if (sponsorship.Seconds < IntervalSeconds)
                continue;

            // Consume this interval once, even if the bank cannot confirm the deposit.
            sponsorship.Seconds %= IntervalSeconds;
            if (!_preferences.TryGetCachedPreferences(user, out var preferences) ||
                preferences.SelectedCharacterIndex != sponsorship.Slot ||
                preferences.SelectedCharacter is not HumanoidCharacterProfile profile ||
                !HasComp<BankAccountComponent>(mob) ||
                !_bank.TryBankDeposit(session, preferences, profile, Payout, out var balance))
                continue;

            _bank.SetConfirmedBankBalance(mob, balance.Value);
            _chat.DispatchServerMessage(session, Loc.GetString("expedition-sponsorship-paid", ("amount", Payout)));
        }
    }
}
