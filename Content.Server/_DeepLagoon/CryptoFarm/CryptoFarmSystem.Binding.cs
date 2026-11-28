using System.Threading.Tasks;
using Content.Server._DeepLagoon.Exchange;
using Content.Server.Database;
using Content.Server._NF.Bank;
using Content.Server.Popups;
using Content.Server.Preferences.Managers;
using Content.Shared.Access.Components;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.CryptoFarm;

public sealed partial class CryptoFarmSystem
{
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private PopupSystem _popups = default!;
    [Dependency] private SharedAudioSystem _bindingAudio = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly HashSet<EntityUid> _binding = new();

    [Dependency] private BankSystem _bindingBank = default!;

    private void InitializeWalletBinding()
    {
        SubscribeLocalEvent<CryptoFarmComponent, InteractUsingEvent>(OnBindWallet);
        SubscribeLocalEvent<CryptoFarmComponent, ExaminedEvent>(OnExamineWallet);
    }

    private bool CanSwipe(EntityUid farm, EntityUid actor, EntityUid card)
        => Exists(farm) && !EntityManager.IsQueuedForDeletion(farm) && Exists(actor) && Exists(card) &&
            HasComp<IdCardComponent>(card) && _hands.IsHolding(actor, card, out _) &&
            _interaction.InRangeUnobstructed(actor, farm) &&
            TryComp<MobStateComponent>(actor, out var state) && state.CurrentState == MobState.Alive;

    private void OnBindWallet(EntityUid uid, CryptoFarmComponent farm, InteractUsingEvent args)
    {
        if (args.Handled || !CanSwipe(uid, args.User, args.Used)) return;
        args.Handled = true;
        if (!EntityManager.System<ExchangeSystem>().Enabled)
        { Popup("disabled", uid, args.User); return; }
        if (!TryComp<ActorComponent>(args.User, out var actor) || actor.PlayerSession.AttachedEntity != args.User ||
            !_bindingBank.TryGetCharacter(actor.PlayerSession, out _, out var profile, out var slot))
        { Popup("no-character", uid, args.User); return; }
        // The swipe confirms the acting character's wallet. Card text is never an account identifier.
        if (farm.WalletUser != Guid.Empty && farm.WalletUser != actor.PlayerSession.UserId.UserId)
        { Popup("another-owner", uid, args.User); return; }
        if (farm.HasPendingMining || !_binding.Add(uid))
        { Popup("busy", uid, args.User); return; }
        _ = BindWallet(uid, farm, args.User, args.Used, actor.PlayerSession, slot, profile.Name);
    }

    private async Task BindWallet(EntityUid uid, CryptoFarmComponent farm, EntityUid actor, EntityUid card,
        ICommonSession session, int slot, string character)
    {
        try
        {
            var wallet = await _database.ReadExchangeAsync(session.UserId, slot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!CanSwipe(uid, actor, card) || !TryComp<CryptoFarmComponent>(uid, out var current) ||
                !ReferenceEquals(current, farm) || session.AttachedEntity != actor ||
                !_bindingBank.TryGetCharacter(session, out _, out var selected, out var currentSlot) || currentSlot != slot || selected.Name != character ||
                farm.HasPendingMining || farm.WalletUser != Guid.Empty && farm.WalletUser != session.UserId.UserId)
                return;
            farm.WalletUser = session.UserId.UserId;
            farm.WalletProfileId = wallet.ProfileId;
            farm.WalletCharacter = selected.Name;
            _popups.PopupEntity(Loc.GetString("dl-crypto-farm-bound", ("character", selected.Name)), uid, actor);
            _bindingAudio.PlayPvs(Comp<IdCardComponent>(card).SwipeSound, uid);
        }
        catch (Exception e)
        {
            Log.Warning($"Crypto farm wallet binding failed ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}).");
            Popup("unavailable", uid, actor);
        }
        finally { _binding.Remove(uid); }
    }

    private void OnExamineWallet(EntityUid uid, CryptoFarmComponent farm, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange) return;
        args.PushText(farm.IsBound
            ? Loc.GetString("dl-crypto-farm-wallet", ("character", farm.WalletCharacter))
            : Loc.GetString(farm.WalletUser == Guid.Empty ? "dl-crypto-farm-unbound" : "dl-crypto-farm-missing-character"));
        if (farm.HasPendingMining) args.PushText(Loc.GetString("dl-crypto-farm-pending"));
    }

    private void Popup(string key, EntityUid uid, EntityUid actor)
    {
        if (Exists(uid) && Exists(actor)) _popups.PopupEntity(Loc.GetString("dl-crypto-farm-" + key), uid, actor);
    }
}
