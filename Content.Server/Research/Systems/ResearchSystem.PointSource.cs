using Content.Server.Atmos.EntitySystems;
using Content.Server.Power.EntitySystems;
using Content.Server.Research.Components;
using Content.Shared.Atmos;
using Content.Shared.Research.Components;

namespace Content.Server.Research.Systems;

public sealed partial class ResearchSystem
{
    [Dependency] private AtmosphereSystem _atmosphere = default!;

    private void InitializeSource()
    {
        SubscribeLocalEvent<ResearchPointSourceComponent, ResearchServerGetPointsPerSecondEvent>(OnGetPointsPerSecond);
    }

    private void OnGetPointsPerSecond(Entity<ResearchPointSourceComponent> source, ref ResearchServerGetPointsPerSecondEvent args)
    {
        if (!CanProduce(source))
            return;

        args.Points += source.Comp.PointsPerSecond;
        if (source.Comp.ColdBonusPointsPerSecond <= 0 ||
            _atmosphere.GetContainingMixture(source.Owner) is not { TotalMoles: > Atmospherics.GasMinMoles } air)
            return;

        const float fullBonusTemperature = Atmospherics.T0C - 200f;
        var cooling = Math.Clamp((Atmospherics.T20C - air.Temperature) /
            (Atmospherics.T20C - fullBonusTemperature), 0f, 1f);
        args.Points += (int) (source.Comp.ColdBonusPointsPerSecond * cooling);
    }

    public bool CanProduce(Entity<ResearchPointSourceComponent> source)
    {
        return source.Comp.Active && this.IsPowered(source, EntityManager);
    }
}
