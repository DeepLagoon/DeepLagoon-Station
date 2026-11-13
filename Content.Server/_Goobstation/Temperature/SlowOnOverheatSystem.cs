using Content.Server.Temperature.Components;
using Content.Shared.Damage.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Temperature.Components;

namespace Content.Server._Goobstation.Temperature;

public sealed partial class SlowOnOverheatSystem : EntitySystem
{
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SlowOnOverheatComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SlowOnOverheatComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var overheated = TryComp<TemperatureComponent>(uid, out var temperature)
                && temperature.CurrentTemperature >= comp.OverheatThreshold
                && TryComp<MobStateComponent>(uid, out var mob)
                && mob.CurrentState != MobState.Dead
                && !HasComp<GodmodeComponent>(uid);

            if (!overheated)
            {
                ClearSlowdown(uid);
                continue;
            }

            if (HasComp<OverheatSlowdownComponent>(uid))
                continue;

            AddComp<OverheatSlowdownComponent>(uid);
            _movement.RefreshMovementSpeedModifiers(uid);

            var msg = Loc.GetString(comp.OverheatPopup, ("name", Identity.Name(uid, EntityManager)));
            _popup.PopupEntity(msg, uid, PopupType.LargeCaution);
        }
    }

    private void OnShutdown(Entity<SlowOnOverheatComponent> ent, ref ComponentShutdown args)
    {
        if (!TerminatingOrDeleted(ent))
            ClearSlowdown(ent);
    }

    private void ClearSlowdown(EntityUid uid)
    {
        if (RemComp<OverheatSlowdownComponent>(uid))
            _movement.RefreshMovementSpeedModifiers(uid);
    }
}
