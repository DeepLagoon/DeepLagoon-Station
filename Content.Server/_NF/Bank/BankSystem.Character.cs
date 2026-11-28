using System.Diagnostics.CodeAnalysis;
using Content.Server.Preferences.Managers;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Shared._NF.Bank.Components;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Preferences;
using Robust.Shared.Player;

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem
{
    private void InitializeCharacterAccounts()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(args => PreparePlayerSpawn(args.Mob, args.Player, args.Profile));
        SubscribeLocalEvent<BankCharacterComponent, MindAddedMessage>((body, owner, args) => CopyCharacter(owner, args.Mind.Owner));
    }

    public void PreparePlayerSpawn(EntityUid body, ICommonSession? session, ICharacterProfile? profile)
    {
        if (session == null || profile == null || HasComp<BankCharacterComponent>(body) || HasComp<GhostComponent>(body) ||
            !_prefsManager.TryGetCachedPreferences(session.UserId, out var prefs)) return;
        var slot = prefs.IndexOfCharacter(profile);
        if (slot < 0) return;
        var identity = _db.GetRecoveryProfile(session.UserId.UserId, slot);
        _tasks.BlockWaitOnTask(identity);
        if (identity.GetAwaiter().GetResult() is not { } id) return;
        var owner = EnsureComp<BankCharacterComponent>(body);
        owner.User = session.UserId.UserId; owner.Slot = slot; owner.Profile = id;
        if (TryComp<MindContainerComponent>(body, out var container) && container.Mind is { } mind) CopyCharacter(owner, mind);
    }

    private void CopyCharacter(BankCharacterComponent source, EntityUid target)
    {
        var owner = EnsureComp<BankCharacterComponent>(target);
        owner.User = source.User; owner.Slot = source.Slot; owner.Profile = source.Profile;
    }

    // Called before PlayerAttachedEvent, so cloning and mind transfers cannot bind a new body to lobby selection.
    public void MindBodyChanged(EntityUid mind, EntityUid body)
    {
        if (HasComp<GhostComponent>(body)) return;
        if (!HasComp<BankCharacterComponent>(body) && TryComp<RecoveryBodyComponent>(body, out var recovered) && recovered.Slot >= 0)
        {
            var restored = EnsureComp<BankCharacterComponent>(body);
            restored.User = recovered.User; restored.Slot = recovered.Slot; restored.Profile = recovered.Profile;
        }
        if (TryComp<BankCharacterComponent>(body, out var owner)) CopyCharacter(owner, mind);
        else if (TryComp<BankCharacterComponent>(mind, out owner)) CopyCharacter(owner, body);
    }

    public bool TryGetCharacter(ICommonSession session, [NotNullWhen(true)] out PlayerPreferences? prefs,
        [NotNullWhen(true)] out HumanoidCharacterProfile? profile, out int slot)
    {
        profile = null; slot = -1;
        if (!_prefsManager.TryGetCachedPreferences(session.UserId, out prefs)) return false;
        slot = prefs.SelectedCharacterIndex;
        if (session.AttachedEntity is { } body)
        {
            if (HasComp<GhostComponent>(body)) return false;
            if (!TryComp<BankCharacterComponent>(body, out var owner))
            {
                // Bodies restored after a crash already carry their original immutable identity.
                if (TryComp<RecoveryBodyComponent>(body, out var recovered) && recovered.Slot >= 0)
                {
                    owner = EnsureComp<BankCharacterComponent>(body);
                    owner.User = recovered.User; owner.Slot = recovered.Slot; owner.Profile = recovered.Profile;
                }
                else PreparePlayerSpawn(body, session, prefs.SelectedCharacter);
                if (!TryComp(body, out owner)) return false;
            }
            if (owner.User != session.UserId.UserId) return false;
            slot = owner.Slot;
        }
        if (!prefs.Characters.TryGetValue(slot, out var selected) || selected is not HumanoidCharacterProfile character) return false;
        profile = character; return true;
    }

    public bool IsCharacter(ICommonSession session, int slot)
        => TryGetCharacter(session, out _, out _, out var current) && current == slot;

    private int? ExpectedProfile(ICommonSession session, int slot)
        => session.AttachedEntity is { } body && TryComp<BankCharacterComponent>(body, out var owner) &&
           owner.User == session.UserId.UserId && owner.Slot == slot ? owner.Profile : null;

    private bool IsBodyCharacter(EntityUid body, Guid user, int slot)
        => TryComp<BankCharacterComponent>(body, out var owner) && owner.User == user && owner.Slot == slot;
}
