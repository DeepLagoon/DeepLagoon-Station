using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Exchange;
using Content.Server._NF.Bank;
using Content.Server.Administration.Managers;
using Content.Server.Afk;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Construction.Components;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Materials;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Economy;

[CVarDefs]
public sealed class SectorEconomyCVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("economy.enabled", false, CVar.SERVERONLY);
    public static readonly CVarDef<bool> CountAdmins = CVarDef.Create("economy.count_admins", false, CVar.SERVERONLY,
        "Count active living administrators during local economy tests. Keep false for normal play.");
    public static readonly CVarDef<bool> MaintenanceEnabled = CVarDef.Create("economy.maintenance_enabled", false, CVar.SERVERONLY);
    public static readonly CVarDef<int> HourlyPerPlayer = CVarDef.Create("economy.procurement_per_player_hour", 20000, CVar.SERVERONLY);
    public static readonly CVarDef<int> ProtectedDaily = CVarDef.Create("economy.protected_daily", 60000, CVar.SERVERONLY);
    public static readonly CVarDef<int> SmallSale = CVarDef.Create("economy.small_sale", 8000, CVar.SERVERONLY);
    public static readonly CVarDef<float> ProtectedShare = CVarDef.Create("economy.protected_share", .4f, CVar.SERVERONLY);
    public static readonly CVarDef<int> FoodPerPlayerHour = CVarDef.Create("economy.food_per_player_hour", 1500, CVar.SERVERONLY);
    public static readonly CVarDef<int> MealReward = CVarDef.Create("economy.meal_reward", 1500, CVar.SERVERONLY);
    public static readonly CVarDef<int> NfsdPayrollHour = CVarDef.Create("economy.nfsd_payroll_hour", 250000, CVar.SERVERONLY);
    public static readonly CVarDef<int> MedicalPayrollHour = CVarDef.Create("economy.medical_payroll_hour", 65000, CVar.SERVERONLY);
    public static readonly CVarDef<float> PayrollBufferHours = CVarDef.Create("economy.payroll_buffer_hours", 2f, CVar.SERVERONLY);
}

public sealed partial class SectorEconomySystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IAfkManager _afk = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private GameTicker _ticker = default!;
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly Dictionary<string, long> _purchases = new();
    private readonly Dictionary<string, double> _materials = new();
    private readonly Dictionary<string, Dictionary<string, double>> _bills = new();
    private EconomyFinance _finance = new();
    private TimeSpan _nextSample, _nextMaterials, _nextFood;
    private bool _sampling, _readingMaterials, _expiring;
    private decimal _foodAllowance;
    private int _active;
    public bool Enabled => _config.GetCVar(SectorEconomyCVars.Enabled);
    public int MealReward => Math.Max(0, _config.GetCVar(SectorEconomyCVars.MealReward));
    public bool Ready { get; private set; }
    public EconomyFinance Finance => _finance;
    public static long UtcNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public EconomyPolicy Policy => new(Math.Clamp(_config.GetCVar(SectorEconomyCVars.HourlyPerPlayer), 1, 1000000),
        (decimal)Math.Clamp(_config.GetCVar(SectorEconomyCVars.ProtectedShare), 0, 1),
        Math.Max(0, _config.GetCVar(SectorEconomyCVars.ProtectedDaily)), Math.Max(0, _config.GetCVar(SectorEconomyCVars.SmallSale)));

    public override void Initialize() => _config.OnValueChanged(SectorEconomyCVars.Enabled, OnEnabled, true);
    public override void Shutdown() => _config.UnsubValueChanged(SectorEconomyCVars.Enabled, OnEnabled);
    private void OnEnabled(bool enabled)
    {
        Ready = false;
        _nextSample = _nextMaterials = TimeSpan.Zero;
        if (enabled) _config.SetCVar(ExchangeCVars.Enabled, true);
    }

    public override void Update(float frameTime)
    {
        if (!Enabled) return;
        var now = _timing.RealTime;
        if (_ticker.RunLevel == GameRunLevel.InRound && _active > 0)
        {
            var rate = _active * (decimal)Math.Max(0, _config.GetCVar(SectorEconomyCVars.FoodPerPlayerHour));
            _foodAllowance = Math.Min(rate * 2, _foodAllowance + rate * (decimal)Math.Clamp(frameTime, 0, 1) / 3600);
        }
        if (!_sampling && now >= _nextSample)
        {
            _nextSample = now + TimeSpan.FromSeconds(10);
            _active = _ticker.RunLevel != GameRunLevel.InRound ? 0 : _players.Sessions.Where(s =>
                (_config.GetCVar(SectorEconomyCVars.CountAdmins) || !_admins.IsAdmin(s)) &&
                !_afk.IsAfk(s) && s.AttachedEntity is { } actor && !HasComp<GhostComponent>(actor) &&
                TryComp<MobStateComponent>(actor, out var state) && state.CurrentState is MobState.Alive or MobState.Critical)
                .Select(s => s.UserId).Distinct().Count();
            var purchases = _purchases.Select(p => new EconomyPurchase(p.Key, p.Value)).ToArray();
            _purchases.Clear();
            _ = Sample(purchases);
        }
        if (!_readingMaterials && now >= _nextMaterials)
        {
            _nextMaterials = now + TimeSpan.FromMinutes(1);
            _ = ReadMaterials();
        }
        if (now >= _nextFood)
        {
            _nextFood = now + TimeSpan.FromMinutes(1);
            var query = EntityQueryEnumerator<StationCargoBountyDatabaseComponent>();
            while (query.MoveNext(out var station, out var bounties))
                EntityManager.System<CargoSystem>().FillBountyDatabase(station, bounties);
            // Upgrade/crash recovery must adopt the durable market cycle before legacy deadlines are evaluated.
            if (!_expiring && EntityManager.System<PlayerTradeSystem>().TradeLifecycleReady) _ = Expire();
        }
    }

    private async Task Sample(EconomyPurchase[] purchases)
    {
        _sampling = true;
        try { _finance = await _database.SampleSectorEconomyAsync(_epoch, UtcNow, _active, Policy, purchases); Ready = true; }
        catch (Exception e) { Ready = false; Log.Warning($"Economy sample deferred ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}); no offline issuance is inferred."); }
        finally { _sampling = false; }
    }
    private async Task ReadMaterials()
    {
        _readingMaterials = true;
        try
        {
            var snapshot = await _database.ReadSectorEconomyAsync();
            foreach (var (id, value) in snapshot.MaterialFactors)
            {
                var previous = _materials.GetValueOrDefault(id, 1);
                _materials[id] = Math.Clamp(value, previous - .05, previous + .05);
            }
        }
        catch (Exception e) { Log.Warning($"Material index refresh deferred ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)})."); }
        finally { _readingMaterials = false; }
    }
    private async Task Expire()
    {
        _expiring = true;
        try { await _database.ExpireTradeOffersAsync(UtcNow); }
        catch (Exception e) { Log.Warning($"Market expiry deferred ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)})."); }
        finally { _expiring = false; }
    }

    public void Purchased(string key, long units = 1)
    {
        if (!Enabled || units <= 0 || _purchases.Count >= 4096 && !_purchases.ContainsKey(key)) return;
        if (key.StartsWith("vendor:", StringComparison.Ordinal) || key.StartsWith("cargo:", StringComparison.Ordinal))
        {
            var prototype = key[(key.IndexOf(':') + 1)..];
            if (_prototypes.TryIndex<EntityPrototype>(prototype, out var source) && MaterialBill(source).ContainsKey("Antimatter"))
                _purchases["resource:Antimatter"] = Math.Min(1000000, _purchases.GetValueOrDefault("resource:Antimatter") + units);
        }
        _purchases[key] = Math.Min(1000000, _purchases.GetValueOrDefault(key) + Math.Min(units, 1000000));
    }
    public double AntimatterIndex => Enabled ? _finance.DemandFactor("resource:Antimatter", UtcNow) : 1;
    public long Quote(string key, long basis, EntityPrototype? prototype = null)
    {
        if (!Enabled || basis <= 0) return basis;
        var weights = prototype == null ? new Dictionary<string, double> { ["Steel"] = .6, ["Plasteel"] = .2, ["Glass"] = .1, ["Copper"] = .1 }
            : MaterialBill(prototype);
        if (weights.ContainsKey("Antimatter")) _materials["Antimatter"] = _finance.DemandFactor("resource:Antimatter", UtcNow);
        var total = weights.Values.Sum();
        var index = total <= 0 ? 1 : weights.Sum(p => _materials.GetValueOrDefault(p.Key, 1) * p.Value) / total;
        var demand = Math.Max(_finance.DemandFactor(key, UtcNow),
            1 + Math.Min(20, _purchases.GetValueOrDefault(key)) * .02);
        return Math.Clamp((long)Math.Round(basis * Math.Clamp(index * demand, .65, 2), MidpointRounding.AwayFromZero), 1, int.MaxValue);
    }
    private Dictionary<string, double> MaterialBill(EntityPrototype prototype)
    {
        if (_bills.TryGetValue(prototype.ID, out var cached)) return cached;
        var source = prototype;
        if (prototype.TryComp<FlatpackComponent>(out var flatpack, EntityManager.ComponentFactory) && flatpack.Entity is { } machine &&
            _prototypes.TryIndex(machine, out var unpacked)) source = unpacked;
        var result = new Dictionary<string, double>();
        if (source.TryComp<PhysicalCompositionComponent>(out var composition, EntityManager.ComponentFactory))
            foreach (var (id, units) in composition.MaterialComposition)
                if (_prototypes.TryIndex(id, out var material)) result[id.Id] = units * (double)material.Price;
        if (result.Count == 0)
        {
            if (prototype.ID.Contains("Clothing", StringComparison.Ordinal)) { result["Cloth"] = .8; result["Durathread"] = .2; }
            else if (prototype.ID.Contains("Plasma", StringComparison.Ordinal)) result["Plasma"] = 1;
            else if (prototype.ID.Contains("Ame", StringComparison.Ordinal) || prototype.ID.Contains("Antimatter", StringComparison.Ordinal)) result["Antimatter"] = 1;
            else { result["Steel"] = .5; result["Plasteel"] = .2; result["Copper"] = .2; result["Plastic"] = .1; }
        }
        return _bills[prototype.ID] = result;
    }

    public long PayrollHourly(SectorBankAccount account) => account switch
    {
        SectorBankAccount.Nfsd => Math.Max(0, _config.GetCVar(SectorEconomyCVars.NfsdPayrollHour)),
        SectorBankAccount.Medical => Math.Max(0, _config.GetCVar(SectorEconomyCVars.MedicalPayrollHour)), _ => 0,
    };
    public long PayrollReserve(SectorBankAccount account)
    {
        if (!Enabled) return 0;
        var hours = _ticker.RunLevel == GameRunLevel.PreRoundLobby ? 0 : Math.Max(0, _ticker.RoundDuration().TotalHours);
        var expected = (decimal)(hours + Math.Max(0, _config.GetCVar(SectorEconomyCVars.PayrollBufferHours))) * PayrollHourly(account);
        return (long)Math.Clamp(decimal.Ceiling(expected - _bank.LedgerAmount(account, LedgerEntryType.StationWithdrawalPayroll)), 0, long.MaxValue);
    }
    public bool AllowsSpending(SectorBankAccount account, long balance, long amount, LedgerEntryType reason)
        => !Enabled || reason == LedgerEntryType.StationWithdrawalPayroll || balance - amount >= PayrollReserve(account);

    public bool TryFundFood(long amount, out SectorBankAccount account)
    {
        account = SectorBankAccount.Invalid;
        if (!Enabled || amount <= 0 || _foodAllowance < amount) return false;
        foreach (var candidate in new[] { SectorBankAccount.Frontier, SectorBankAccount.Mieyo, SectorBankAccount.BlackMarket, SectorBankAccount.Nfsd, SectorBankAccount.Medical })
            if (_bank.TrySectorWithdraw(candidate, amount, LedgerEntryType.StationWithdrawalBounty))
            {
                account = candidate;
                _foodAllowance -= amount;
                return true;
            }
        return false;
    }
    public void RefundFood(SectorBankAccount account, long amount)
    {
        if (amount <= 0) return;
        if (_bank.TrySectorDeposit(account, amount, LedgerEntryType.StationDepositOther)) _foodAllowance += amount;
    }
}
