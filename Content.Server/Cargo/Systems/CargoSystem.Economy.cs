using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server._DeepLagoon.Economy;
using Content.Server._DeepLagoon.Procurement;
using Content.Server.Cargo.Components;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._Mono.Company;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Examine;
using Content.Shared.Preferences;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    [Dependency] private IServerDbManager _economyDb = default!;
    [Dependency] private IServerPreferencesManager _economyPrefs = default!;
    [Dependency] private SharedInteractionSystem _economyInteraction = default!;
    private readonly HashSet<EntityUid> _economyConsoles = new();
    private readonly Dictionary<EntityUid, string> _economyStatuses = new();
    private readonly Dictionary<Guid, NpcSale> _economySales = new();
    private readonly HashSet<(EntityUid Station, string Bounty)> _economyBounties = new();
    private TimeSpan _nextEconomyRetry;
    private bool _economyCreditMode;
    private sealed class NpcSale
    {
        public required EntityUid Console, Escrow;
        public required EntityUid[] Roots, Entities;
        public required Dictionary<EntityUid, EntityCoordinates> Positions;
        public required EntityUid Grid;
        public required NetUserId User;
        public required int Slot;
        public required EconomyProcurement Request;
        public required ProcurementSale Observation;
        public required (EntityUid Station, string Bounty)[] Bounties;
        public required Dictionary<SectorBankAccount, double> Taxes;
        public string Currency = "Credit";
        public int ForeignPayout;
        public int CashBonus;
        public bool CashBonusIssued;
        public Dictionary<string, FixedPoint2>? Injections;
        public ProtoId<CompanyPrototype>? Company;
        public bool Busy;
        public bool Notified, ForeignIssued, Injected, Journaled;
        public readonly HashSet<SectorBankAccount> ProcessedTaxes = new();
    }

    private string EconomySaleStatus(EntityUid console)
        => EntityManager.System<SectorEconomySystem>().Enabled
            ? _economyStatuses.GetValueOrDefault(console, "") : "";

    private void OnProcurementExamine(Entity<CargoPalletConsoleComponent> ent, ref ExaminedEvent args)
    {
        if (EntityManager.System<SectorEconomySystem>().Enabled && args.IsInDetailsRange)
            args.PushMarkup(Loc.GetString("dl-economy-procurement-intro"));
    }
    private void EconomyStatus(EntityUid console, string key)
    {
        _economyStatuses[console] = Loc.GetString("dl-economy-" + key);
        if (TryComp<CargoPalletConsoleComponent>(console, out var comp)) UpdatePalletConsoleInterface((console, comp));
    }

    public EntityUid[] TradingPalletItems(EntityUid console)
    {
        if (!Exists(console) || Transform(console).GridUid is not { } grid) return [];
        var items = new HashSet<EntityUid>();
        foreach (var (pallet, _, _) in GetCargoPallets(console, grid, BuySellType.Sell))
            _lookup.GetEntitiesIntersecting(pallet, items, LookupFlags.Dynamic | LookupFlags.Sundries);
        return items.Where(uid => Exists(uid) && !EntityManager.IsQueuedForDeletion(uid) && Transform(uid).ParentUid == grid &&
            !Transform(uid).Anchored && CanSell(uid, Transform(uid))).Take(128).ToArray();
    }

    private async void BeginEconomySale(EntityUid console, CargoPalletConsoleComponent component, EntityUid actor)
    {
        var economy = EntityManager.System<SectorEconomySystem>();
        if (!economy.Ready || _economySales.Count >= 64 || !TryComp<ActorComponent>(actor, out var actorComp) || !_economyConsoles.Add(console)) return;
        var session = actorComp.PlayerSession;
        var payment = false;
        var handedOff = false;
        try
        {
            if (!_economyPrefs.TryGetCachedPreferences(session.UserId, out var prefs) || prefs.SelectedCharacter is not HumanoidCharacterProfile)
                return;
            if (!_bank.BeginExchangePayment(session.UserId)) { EconomyStatus(console, "busy"); return; }
            payment = true;
            var slot = prefs.SelectedCharacterIndex;
            var account = await _economyDb.ReadEconomyAccountAsync(session.UserId.UserId, slot);
            if (!Exists(console) || session.AttachedEntity != actor || !_uiSystem.IsUiOpen(console, CargoPalletConsoleUiKey.Sale, actor) ||
                !_economyInteraction.InRangeUnobstructed(actor, console) || Transform(console).GridUid is not { } grid)
                return;
            var quotes = new List<ProcurementRoot>();
            var values = new Dictionary<int, ProcurementValuation>();
            GetPalletGoods((console, component), grid, out var roots, out var price, out var unmodified,
                out var black, out var frontier, out var nfsd, out var medical, out var injections, out var company, quotes, values);
            if (roots.Count == 0 || !double.IsFinite(price + unmodified)) return;
            if (price + unmodified > int.MaxValue) { EconomyStatus(console, "manifest-too-large"); return; }
            var gross = (int)Math.Clamp(price + unmodified, 0, int.MaxValue);
            var observation = _procurement.CapturePallet(console, grid, actor, quotes, values, gross, component.CashType.Id);
            if (observation is not { ManifestComplete: true }) { EconomyStatus(console, "manifest-too-large"); return; }
            var bountyLocks = new List<(EntityUid, string)>();
            long creditBonus = 0, fundedBonus = 0;
            foreach (var root in roots)
                if (TryGetBountyLabel(root, out _, out var label) && label.AssociatedStationId is { } station &&
                    TryGetBountyFromId(station, label.Id, out var bounty) && _protoMan.TryIndex(bounty.Value.Bounty, out var proto) && IsBountyComplete(root, proto))
                {
                    var key = (station, label.Id);
                    if (_economyBounties.Contains(key) || bountyLocks.Contains(key)) { EconomyStatus(console, "busy"); return; }
                    bountyLocks.Add(key);
                    if (bounty.Value.FundedReward >= 0 || IsCreditReward(proto))
                    {
                        var reward = EffectiveBountyReward(bounty.Value, proto);
                        creditBonus += reward;
                        if (bounty.Value.FundedReward >= 0) fundedBonus += reward;
                    }
                }
            var taxes = new Dictionary<SectorBankAccount, double>
            { [SectorBankAccount.BlackMarket] = black, [SectorBankAccount.Frontier] = frontier,
                [SectorBankAccount.Nfsd] = nfsd, [SectorBankAccount.Medical] = medical };
            var principal = component.CashType == "Credit" ? Math.Min(gross, observation.ReturnedPrincipal) : 0;
            var newPayout = (component.CashType == "Credit" ? gross - principal : gross) + creditBonus - fundedBonus;
            var overhead = taxes.Values.Where(v => v > 0).Sum(v => (long)v)
                + (injections?.Values.Where(v => v > FixedPoint2.Zero).Sum(v => (long)Math.Ceiling(v.Double())) ?? 0);
            var costs = AllocateProcurement(newPayout + overhead, observation);
            var creditPayout = (component.CashType == "Credit" ? gross : 0L) + creditBonus;
            if (creditPayout > int.MaxValue || newPayout > int.MaxValue) { EconomyStatus(console, "manifest-too-large"); return; }
            var entities = new HashSet<EntityUid>(roots);
            var pending = new Stack<EntityUid>(roots);
            while (pending.TryPop(out var current))
            {
                var children = Transform(current).ChildEnumerator;
                while (children.MoveNext(out var child)) if (entities.Add(child)) pending.Push(child);
                if (entities.Count > 4096) { EconomyStatus(console, "manifest-too-large"); return; }
            }
            if (_economySales.Values.Sum(s => s.Entities.Length) + entities.Count > 16384) { EconomyStatus(console, "busy"); return; }
            var recovery = EntityManager.System<CrashRecoverySystem>();
            var removal = recovery.ConsumedItems(entities);
            var escrow = Spawn(null, MapCoordinates.Nullspace);
            EntityManager.System<MetaDataSystem>().SetEntityPaused(escrow, true);
            var container = _container.EnsureContainer<Container>(escrow, "procurement");
            recovery.RegisterEscrow(escrow, actor);
            var positions = roots.ToDictionary(uid => uid, uid => Transform(uid).Coordinates);
            foreach (var root in roots)
                if (!_container.Insert(root, container))
                {
                    foreach (var previous in roots.Where(r => _container.TryGetContainingContainer((r, null, null), out var held) && held.Owner == escrow))
                    { _container.TryRemoveFromContainer(previous, force: true); _transformSystem.SetCoordinates(previous, positions[previous]); }
                    QueueDel(escrow);
                    EconomyStatus(console, "busy");
                    return;
                }
            foreach (var key in bountyLocks) _economyBounties.Add(key);
            // The receipt spends the procurement budget; the console dispenses the physical payment.
            var request = new EconomyProcurement(observation.Id, session.UserId.UserId, account.Profile, 0,
                0, costs, removal, QuotaPayout: newPayout);
            var job = new NpcSale { Console = console, Grid = grid, Escrow = escrow, User = session.UserId, Slot = slot,
                Roots = roots.ToArray(), Entities = entities.ToArray(), Positions = positions, Request = request, Observation = observation,
                Bounties = bountyLocks.ToArray(), Taxes = taxes, Currency = component.CashType.Id,
                ForeignPayout = component.CashType == "Credit" ? (int)creditPayout : gross,
                CashBonus = component.CashType != "Credit" ? (int)creditBonus : 0,
                Injections = injections, Company = company };
            _economySales.Add(request.Operation, job);
            handedOff = true;
            EconomyStatus(console, "processing");
            await CompleteEconomySale(job);
        }
        catch (Exception e) { Log.Warning($"Procurement preparation failed ({e.GetType().Name})."); EconomyStatus(console, "unavailable"); }
        finally
        {
            if (!handedOff)
            {
                _economyConsoles.Remove(console);
                if (payment) _bank.EndExchangePayment(session.UserId);
            }
        }
    }

    private static Dictionary<string, long> AllocateProcurement(long total, ProcurementSale sale)
    {
        var weights = sale.Items.Where(i => i.OwnPayout is > 0 && i.SuggestedCategory != ProcurementCategory.Principal)
            .GroupBy(i => i.SuggestedCategory == ProcurementCategory.Unclassified ? "Industry" : i.SuggestedCategory.ToString())
            .ToDictionary(g => g.Key, g => g.Sum(i => (decimal)i.OwnPayout!.Value));
        if (weights.Count == 0) weights["Industry"] = 1;
        var sum = weights.Values.Sum();
        var result = weights.ToDictionary(p => p.Key, p => (long)decimal.Floor(total * p.Value / sum));
        result[result.Keys.OrderBy(k => k, StringComparer.Ordinal).First()] += total - result.Values.Sum();
        return result;
    }
    private bool IsCreditReward(CargoBountyPrototype prototype)
        => _protoMan.TryIndex(prototype.RewardProto, out var entity) && entity.TryComp<StackComponent>(out var stack, EntityManager.ComponentFactory) && stack.StackTypeId == "Credit";
    private static int EffectiveBountyReward(Content.Shared.Cargo.CargoBountyData data, CargoBountyPrototype prototype)
        => data.FundedReward >= 0 ? data.FundedReward : prototype.Reward;

    private async Task CompleteEconomySale(NpcSale job)
    {
        if (job.Busy) return;
        job.Busy = true;
        try
        {
            var economy = EntityManager.System<SectorEconomySystem>();
            var result = await _economyDb.PayProcurementAsync(job.Request, economy.Policy, SectorEconomySystem.UtcNow);
            if (!result.Accepted)
            {
                foreach (var root in job.Roots.Where(Exists))
                {
                    _container.TryRemoveFromContainer(root, force: true);
                    if (Exists(job.Positions[root].EntityId)) _transformSystem.SetCoordinates(root, job.Positions[root]);
                }
                FinishEconomySale(job, "procurement-exhausted");
                return;
            }
            var oldObservation = _procurementPallet;
            var oldCredit = _economyCreditMode;
            _procurementPallet = job.Observation.Id;
            _economyCreditMode = true;
            try { if (!job.Notified) { job.Notified = true; NotifyEntitiesSold(job.Roots.Where(Exists).ToHashSet(), job.Grid); } }
            finally { _procurementPallet = oldObservation; _economyCreditMode = oldCredit; }
            foreach (var (account, value) in job.Taxes)
            {
                if (!job.ProcessedTaxes.Add(account)) continue;
                var reason = account switch { SectorBankAccount.Frontier => LedgerEntryType.ColonialOutpostSales,
                    SectorBankAccount.Nfsd => LedgerEntryType.TSFMCSales, SectorBankAccount.Medical => LedgerEntryType.MedicalSales,
                    _ => LedgerEntryType.BlackMarketSales };
                if (value > 0 && _bank.TrySectorDeposit(account, (long)value, reason)) job.Observation.FactionAccruals[account.ToString()] = (long)value;
                else if (value < 0) _bank.TrySectorWithdraw(account, (long)-value, account switch
                { SectorBankAccount.Frontier => LedgerEntryType.ColonialOutpostPenalties,
                    SectorBankAccount.Nfsd => LedgerEntryType.TSFMCPenalties, SectorBankAccount.Medical => LedgerEntryType.MedicalPenalties,
                    _ => LedgerEntryType.BlackMarketPenalties });
            }
            if (!job.ForeignIssued && job.ForeignPayout > 0 && Exists(job.Console))
            {
                job.ForeignIssued = true;
                _stack.Spawn(job.ForeignPayout, _protoMan.Index<StackPrototype>(job.Currency), Transform(job.Console).Coordinates);
            }
            if (!job.CashBonusIssued && job.CashBonus > 0 && Exists(job.Console))
            {
                job.CashBonusIssued = true;
                _stack.Spawn(job.CashBonus, _protoMan.Index<StackPrototype>("Credit"), Transform(job.Console).Coordinates);
            }
            if (!job.Injected && job.Company != null && job.Injections != null)
            { job.Injected = true; _currencyInjection.InjectCurrency(job.Company.Value, job.Injections); }
            foreach (var root in job.Roots.Where(Exists)) Del(root);
            if (!job.Journaled) { job.Journaled = true; _procurement.CommitPallet(job.Observation); }
            FinishEconomySale(job, "procurement-paid");
        }
        catch (InvalidOperationException e) when (e.Message == "character_missing")
        {
            foreach (var root in job.Roots.Where(Exists))
            {
                _container.TryRemoveFromContainer(root, force: true);
                if (Exists(job.Positions[root].EntityId)) _transformSystem.SetCoordinates(root, job.Positions[root]);
            }
            FinishEconomySale(job, "character-missing");
        }
        catch (Exception e)
        {
            Log.Warning($"Procurement receipt {job.Request.Operation} awaits confirmation ({e.GetType().Name}).");
            if (Exists(job.Console)) EconomyStatus(job.Console, "processing");
        }
        finally { job.Busy = false; }
    }
    private void FinishEconomySale(NpcSale job, string status)
    {
        foreach (var key in job.Bounties) _economyBounties.Remove(key);
        _economySales.Remove(job.Request.Operation);
        _economyConsoles.Remove(job.Console);
        _bank.EndExchangePayment(job.User);
        if (Exists(job.Escrow)) QueueDel(job.Escrow);
        if (Exists(job.Console)) EconomyStatus(job.Console, status);
    }
    private void UpdateEconomySales()
    {
        if (_timing.RealTime < _nextEconomyRetry) return;
        _nextEconomyRetry = _timing.RealTime + TimeSpan.FromSeconds(5);
        foreach (var job in _economySales.Values.Where(j => !j.Busy).Take(8).ToArray()) _ = CompleteEconomySale(job);
    }
}
