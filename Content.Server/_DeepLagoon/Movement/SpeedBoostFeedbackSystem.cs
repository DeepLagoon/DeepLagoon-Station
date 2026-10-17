using Content.Server.Chat.Managers;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Movement;

/// <summary>Private, authoritative sprint feedback, once per movement state transition.</summary>
public sealed class SpeedBoostFeedbackSystem : EntitySystem
{
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<InputMoverComponent, SpeedBoostChangedEvent>(OnChanged);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnDetached);
    }

    private void OnChanged(Entity<InputMoverComponent> entity, ref SpeedBoostChangedEvent args)
    {
        if (!TryComp<ActorComponent>(entity, out var actor))
            return;

        var message = args.Active ? "speed-boost-started" : args.Exhausted ? "speed-boost-exhausted" : "speed-boost-stopped";
        _chat.DispatchServerMessage(actor.PlayerSession, Loc.GetString(message), suppressLog: true);
        _audio.PlayGlobal("/Audio/UserInterface/click.ogg", Filter.SinglePlayer(actor.PlayerSession), true,
            AudioParams.Default.WithVolume(-8f).WithPitchScale(args.Active ? 1.2f : 0.8f));
    }

    private void OnDetached(PlayerDetachedEvent args)
    {
        if (TryComp<InputMoverComponent>(args.Entity, out var mover))
            mover.SpeedBoostActive = false;
    }
}
