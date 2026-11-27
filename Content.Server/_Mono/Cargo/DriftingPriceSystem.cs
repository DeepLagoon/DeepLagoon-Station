using System.Linq;
using Content.Server.Cargo.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Content.Shared.Stacks;
using Content.Server.Database;
using Content.Server._DeepLagoon.Exchange;
using Robust.Shared.Configuration;

namespace Content.Server._Mono.Cargo;

public sealed partial class DriftingPriceSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IConfigurationManager _config = default!;

    private float _updateSpacing = 1f;
    private float _updateAccum = 0f;

    public override void Initialize()
    {
        SubscribeLocalEvent<DriftingPriceComponent, MapInitEvent>(OnInit);
        SubscribeLocalEvent<DriftingPriceComponent, PriceCalculationEvent>(OnGetPrice);
        SubscribeLocalEvent<DriftingPriceComponent, StackSplitEvent>(OnStackSplit);
    }

    private void OnInit(Entity<DriftingPriceComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.CurrentPrice = _random.NextDouble(ent.Comp.MinInitial, ent.Comp.MaxInitial);
    }

    private void OnGetPrice(Entity<DriftingPriceComponent> ent, ref PriceCalculationEvent args)
    {
        if (_config.GetCVar(ExchangeCVars.Enabled) &&
            ExchangeMarket.Assets.Any(asset => asset.CoinPrototype == MetaData(ent).EntityPrototype?.ID))
        {
            args.Price = 0;
            args.Handled = true;
            return;
        }
        args.Price += ent.Comp.CurrentPrice * (TryComp<StackComponent>(ent, out var stack) ? stack.Count : 1);
    }

    private void OnStackSplit(EntityUid uid, DriftingPriceComponent price, ref StackSplitEvent args)
    {
        if (TryComp<DriftingPriceComponent>(args.NewId, out var split))
            split.CurrentPrice = price.CurrentPrice;
    }

    internal void MergeUnitPrice(EntityUid donor, EntityUid recipient, int recipientCount, int amount)
    {
        if (amount <= 0 || !TryComp<DriftingPriceComponent>(donor, out var source)
            || !TryComp<DriftingPriceComponent>(recipient, out var target))
            return;
        target.CurrentPrice = (target.CurrentPrice * recipientCount + source.CurrentPrice * amount) / (recipientCount + amount);
    }

    public override void Update(float frameTime)
    {
        _updateAccum += frameTime;
        if (_updateAccum < _updateSpacing)
            return;
        _updateAccum -= _updateSpacing;

        var query = EntityQueryEnumerator<DriftingPriceComponent>();
        var exchange = _config.GetCVar(ExchangeCVars.Enabled);
        while (query.MoveNext(out var uid, out var price))
        {
            if (exchange && ExchangeMarket.Assets.Any(asset => asset.CoinPrototype == MetaData(uid).EntityPrototype?.ID))
                continue;
            var driftTotal = price.CurrentPrice * price.DriftRate * _updateSpacing;
            var priceScale = price.CurrentPrice / price.BasePrice;
            var priceOffset = Math.Pow(priceScale, price.Stability);
            var driftPoint = Math.Pow(_random.NextDouble(), priceOffset) * 2 - 1;
            price.CurrentPrice += driftTotal * driftPoint;
        }
    }
}
