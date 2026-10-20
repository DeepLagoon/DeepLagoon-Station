using Content.Shared.Damage.Components;

namespace Content.Shared.Damage.Systems;

public sealed partial class StaminaSystem
{
    public const float SpeedBoostDuration = 12f;

    // Stop just short of combat stamina crit: sprint exhaustion should stop the boost,
    // not knock the runner down or trigger combat jitter/stuttering every tick.
    public static float SpeedBoostCapacity(StaminaComponent stamina) => MathF.Max(0f, stamina.CritThreshold - 0.01f);

    public bool CanSpeedBoost(StaminaComponent stamina) =>
        !stamina.Critical && !stamina.SpeedBoostExhausted && stamina.StaminaDamage < SpeedBoostCapacity(stamina);

    public void ConsumeSpeedBoostStamina(Entity<StaminaComponent> entity, float seconds)
    {
        if (seconds <= 0f || !CanSpeedBoost(entity.Comp))
            return;

        var capacity = SpeedBoostCapacity(entity.Comp);
        entity.Comp.StaminaDamage = MathF.Min(capacity, entity.Comp.StaminaDamage + capacity / SpeedBoostDuration * seconds);
        var nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(MathF.Max(entity.Comp.Cooldown, 0.1f));
        if (entity.Comp.NextUpdate < nextUpdate)
            entity.Comp.NextUpdate = nextUpdate;
        if (entity.Comp.StaminaDamage >= capacity - 0.001f)
        {
            entity.Comp.StaminaDamage = capacity;
            entity.Comp.SpeedBoostExhausted = true;
        }

        EnsureComp<ActiveStaminaComponent>(entity);
        SetStaminaAlert(entity, entity.Comp);
        Dirty(entity);
    }

    public void RecoverSpeedBoost(Entity<StaminaComponent> entity)
    {
        if (!entity.Comp.SpeedBoostExhausted || entity.Comp.Critical ||
            entity.Comp.StaminaDamage > SpeedBoostCapacity(entity.Comp) * 0.8f)
            return;

        entity.Comp.SpeedBoostExhausted = false;
        Dirty(entity);
    }
}
