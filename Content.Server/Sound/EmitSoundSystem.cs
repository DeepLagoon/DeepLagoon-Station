using Content.Server.Explosion.EntitySystems;
using Content.Server.Sound.Components;
using Content.Shared.UserInterface;
using Content.Shared.Sound;
using Content.Shared.Sound.Components;
using Robust.Shared.Timing;
using Robust.Shared.Network;

namespace Content.Server.Sound;

public sealed partial class EmitSoundSystem : SharedEmitSoundSystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private INetManager _net = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var diagnostic = EntityManager.DiagnosticWorkTiming;
        var started = diagnostic != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long playTime = 0;
        var checkedTimers = 0;
        var dueSounds = 0;
        var query = EntityQueryEnumerator<SpamEmitSoundComponent>();

        while (query.MoveNext(out var uid, out var soundSpammer))
        {
            checkedTimers++;
            if (!soundSpammer.Enabled)
                continue;

            if (_timing.CurTime >= soundSpammer.NextSound)
            {
                var playStarted = diagnostic != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (soundSpammer.PopUp != null)
                    Popup.PopupEntity(Loc.GetString(soundSpammer.PopUp), uid);
                TryEmitSound(uid, soundSpammer, predict: false);

                SpamEmitSoundReset((uid, soundSpammer));
                dueSounds++;
                if (diagnostic != null)
                    playTime += System.Diagnostics.Stopwatch.GetTimestamp() - playStarted;
            }
        }
        if (diagnostic != null)
        {
            diagnostic("sound.emit.play", playTime, dueSounds);
            diagnostic("sound.emit.scan", System.Diagnostics.Stopwatch.GetTimestamp() - started - playTime, checkedTimers);
        }
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EmitSoundOnTriggerComponent, TriggerEvent>(HandleEmitSoundOnTrigger);
        SubscribeLocalEvent<SpamEmitSoundComponent, MapInitEvent>(HandleSpamEmitSoundMapInit);
    }

    private void HandleEmitSoundOnTrigger(EntityUid uid, EmitSoundOnTriggerComponent component, TriggerEvent args)
    {
        TryEmitSound(uid, component, args.User, false);
        args.Handled = true;
    }

    private void HandleSpamEmitSoundMapInit(Entity<SpamEmitSoundComponent> entity, ref MapInitEvent args)
    {
        SpamEmitSoundReset(entity);

        // Prewarm so multiple entities have more variation.
        entity.Comp.NextSound -= Random.Next(entity.Comp.MaxInterval);
        Dirty(entity);
    }

    private void SpamEmitSoundReset(Entity<SpamEmitSoundComponent> entity)
    {
        if (_net.IsClient)
            return;

        entity.Comp.NextSound = _timing.CurTime + ((entity.Comp.MinInterval < entity.Comp.MaxInterval)
            ? Random.Next(entity.Comp.MinInterval, entity.Comp.MaxInterval)
            : entity.Comp.MaxInterval);

        Dirty(entity);
    }

    public override void SetEnabled(Entity<SpamEmitSoundComponent?> entity, bool enabled)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return;

        if (entity.Comp.Enabled == enabled)
            return;

        entity.Comp.Enabled = enabled;

        if (enabled)
            SpamEmitSoundReset((entity, entity.Comp));
    }
}
