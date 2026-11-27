using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CryptoFarm;
using Content.Server._DeepLagoon.Procurement;
using Content.Server._Mono.Cargo;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Shared.Materials;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Exchange;

public sealed partial class ExchangeSystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly Queue<EntityUid> _mintQueue = new();
    private readonly HashSet<EntityUid> _mintQueued = new();
    private readonly Dictionary<string, (decimal Paid, long Volume)> _materialSales = new();
    private TimeSpan _nextMint;
    private TimeSpan _nextMarketSample;
    private bool _mintBusy;
    private bool _sampleBusy;
    private static readonly string[] MaterialIds =
        ["Steel", "Glass", "Plasteel", "Plastic", "Cloth", "Durathread", "Copper", "Gold", "Silver", "Uranium", "Plasma", "Titanium", "Diamond"];

    private void InitializeMarketFeeds()
    {
        SubscribeLocalEvent<ProcurementSaleObserved>(OnSale);
    }

    private void OnSale(ProcurementSaleObserved observation)
    {
        if (!Enabled) return;
        foreach (var item in observation.Sale.Items)
        {
            if (item.SuggestedCategory != ProcurementCategory.Materials || item.Materials.Count != 1 ||
                item.OwnPayout is not > 0 || !double.IsFinite(item.OwnPayout.Value)) continue;
            var material = item.Materials.Single();
            if (!MaterialIds.Contains(material.Key) || material.Value <= 0 || material.Value > long.MaxValue) continue;
            var previous = _materialSales.GetValueOrDefault(material.Key);
            _materialSales[material.Key] = (previous.Paid + (decimal)item.OwnPayout.Value,
                checked(previous.Volume + (long)material.Value));
        }
    }

    public void ObservePlayerTrade(string? prototype, long price, Dictionary<string, double>? materials)
    {
        if (!Enabled || price <= 0 || materials?.Count != 1 || prototype == null ||
            !_prototypes.TryIndex<EntityPrototype>(prototype, out var entity) ||
            !entity.TryComp<Content.Shared.Stacks.StackComponent>(out var stack, EntityManager.ComponentFactory)) return;
        var material = materials.Single();
        if (!MaterialIds.Contains(material.Key) || stack.StackTypeId.Id != material.Key || material.Value <= 0 || material.Value > 1000000000) return;
        var previous = _materialSales.GetValueOrDefault(material.Key);
        _materialSales[material.Key] = (previous.Paid + price, checked(previous.Volume + (long)material.Value));
    }

    public bool QueueMint(EntityUid farm)
    {
        if (!Enabled || _mintQueued.Count >= 4096 || !_mintQueued.Add(farm)) return false;
        _mintQueue.Enqueue(farm);
        return true;
    }

    private bool CanMint(EntityUid farm)
        => Exists(farm) && !EntityManager.IsQueuedForDeletion(farm) && !MetaData(farm).EntityPaused &&
            TryComp<CryptoFarmComponent>(farm, out var component) &&
            component.HasPendingMining && component.MiningUser != Guid.Empty && component.MiningProfileId > 0;

    private void UpdateMarketFeeds()
    {
        if (!Enabled) return;
        if (!_mintBusy && _timing.RealTime >= _nextMint && _mintQueue.Count > 0)
        {
            _nextMint = _timing.RealTime + TimeSpan.FromSeconds(5);
            var farms = new List<EntityUid>();
            for (var i = 0; i < 64 && _mintQueue.TryDequeue(out var farm); i++)
            {
                if (CanMint(farm)) farms.Add(farm);
                else _mintQueued.Remove(farm);
            }
            if (farms.Count > 0) _ = MintBatch(farms.ToArray());
        }
        if (!_sampleBusy && _timing.RealTime >= _nextMarketSample)
        {
            _nextMarketSample = _timing.RealTime + TimeSpan.FromMinutes(1);
            var samples = new List<ExchangeMaterialSample>();
            foreach (var id in MaterialIds)
            {
                if (!_prototypes.TryIndex<MaterialPrototype>(id, out var material)) continue;
                var sale = _materialSales.GetValueOrDefault(id);
                samples.Add(new(id, (decimal)material.Price * 100,
                    sale.Volume > 0 ? sale.Paid * 100 / sale.Volume : null, sale.Volume));
            }
            var antimatter = EntityManager.System<Content.Server._DeepLagoon.Economy.SectorEconomySystem>().AntimatterIndex;
            // A consumption index, not a mined physical resource or a fabricated player trade.
            samples.Add(new("AntimatterIndex", 100, (decimal)antimatter * 100, 0, ConsumptionIndex: true));
            _materialSales.Clear();
            _ = SampleMarket(samples.ToArray());
        }
    }

    private async Task MintBatch(EntityUid[] farms)
    {
        _mintBusy = true;
        try
        {
            var requests = farms.Select(uid =>
            {
                var farm = Comp<CryptoFarmComponent>(uid);
                return new ExchangeMiningRequest(farm.MiningOperation, farm.MiningUser, farm.MiningProfileId);
            }).ToArray();
            // Multiple restored representations can briefly share a receipt; send it once to the DB batch.
            var results = await _database.MintExchangeToWalletsAsync(requests.DistinctBy(r => r.Operation).ToArray(), Now);
            var byOperation = results.ToDictionary(r => r.Operation);
            for (var i = 0; i < farms.Length; i++)
            {
                if (!TryComp<CryptoFarmComponent>(farms[i], out var farm) || farm.MiningOperation != requests[i].Operation ||
                    !byOperation.TryGetValue(requests[i].Operation, out var result)) continue;
                farm.MiningOperation = Guid.Empty;
                farm.MiningUser = Guid.Empty;
                farm.MiningProfileId = 0;
                if (result.Error == "character_missing") farm.WalletProfileId = 0;
            }
        }
        catch (Exception e) { Log.Warning($"Crypto issuance postponed ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)})."); }
        finally
        {
            foreach (var farm in farms) _mintQueued.Remove(farm);
            _mintBusy = false;
        }
    }

    private async Task SampleMarket(ExchangeMaterialSample[] samples)
    {
        _sampleBusy = true;
        try { await _database.SampleExchangeAsync(samples, Now); }
        catch (Exception e) { Log.Warning($"Exchange history sample was not saved ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}); no synthetic replacement was created."); }
        finally { _sampleBusy = false; }
    }
}
