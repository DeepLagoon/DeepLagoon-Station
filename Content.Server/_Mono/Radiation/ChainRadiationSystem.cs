using Content.Server.Explosion.EntitySystems;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Server.Radiation.Systems;
using Content.Shared.Explosion.Components;
using Content.Shared.Radiation.Components;
using Robust.Shared.Map;

namespace Content.Server._Mono.Radiation;

public sealed partial class ChainRadiationSystem : EntitySystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ExplosionSystem _explosion = default!;
    [Dependency] private RadiationSystem _radiation = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly HashSet<(EntityCoordinates Position, float Radius)> _searched = new();
    private readonly HashSet<Entity<ChainRadiationComponent>> _nearby = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RadiationSystemUpdatedEvent>(OnUpdate);
    }

    private void OnUpdate(RadiationSystemUpdatedEvent args)
    {
        _searched.Clear();
        var query = EntityQueryEnumerator<ChainRadiationComponent, RadiationReceiverComponent, RadiationSourceComponent>();
        while (query.MoveNext(out var uid, out var chain, out var receiver, out var source))
        {
            source.Intensity = chain.BaseIntensity * (1f + receiver.CurrentRadiation * chain.Coefficient);

            if (source.Intensity > chain.ExplosionThreshold)
            {
                var coord = Transform(uid).Coordinates;
                // Neighbors are moved to the same coordinates to combine their blasts. Their turns
                // in this update would otherwise repeat the same spatial lookup for the same pile.
                if (_searched.Add((coord, chain.ChainExplosionRadius)))
                {
                    _nearby.Clear();
                    _lookup.GetEntitiesInRange(coord, chain.ChainExplosionRadius, _nearby);
                    foreach (var other in _nearby)
                    {
                        if (HasExploded(other))
                            continue;

                        _transform.SetCoordinates(other, coord);
                        Explode(other);
                    }
                }

                if (!HasExploded(uid))
                    Explode((uid, chain));
            }
        }
    }

    private bool HasExploded(EntityUid uid) => TryComp<ExplosiveComponent>(uid, out var explosive) && explosive.Exploded;

    private void Explode(Entity<ChainRadiationComponent> ent)
    {
        var explosive = EnsureComp<ExplosiveComponent>(ent);

        explosive.TotalIntensity = ent.Comp.TotalIntensity;
        explosive.IntensitySlope = ent.Comp.IntensitySlope;
        explosive.MaxIntensity = ent.Comp.MaxIntensity;

        _explosion.TriggerExplosive(ent, explosive);
    }
}
