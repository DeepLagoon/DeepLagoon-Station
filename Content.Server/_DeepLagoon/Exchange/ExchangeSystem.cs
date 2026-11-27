using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server._NF.Bank;
using Content.Server.CartridgeLoader;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.Exchange;
using Content.Shared.CartridgeLoader;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.PDA;
using Content.Shared.Preferences;
using Content.Shared.Stacks;
using Content.Shared._NF.Bank.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Exchange;

[CVarDefs]
public sealed class ExchangeCVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("exchange.enabled", false, CVar.SERVERONLY);
}

[RegisterComponent]
public sealed partial class ExchangeCartridgeComponent : Component;

public sealed partial class ExchangeSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private CartridgeLoaderSystem _loader = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private CrashRecoverySystem _recovery = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    private sealed record Quote(Guid Id, EntityUid Pda, int Slot, int ProfileId, string Asset, long Units,
        long BuyCost, long SellProceeds, int Period, TimeSpan Expires, string WalletAddress, Guid WalletVersion);
    private sealed record TransferQuote(Guid Id, EntityUid Pda, int Slot, int Profile, string Asset, long Units,
        ExchangeRecipient Recipient, TimeSpan Expires, string SenderAddress);
    private sealed record WalletSession(EntityUid Pda, int Slot, int Profile, string Address, TimeSpan Expires, Guid Version);
    private readonly Dictionary<Guid, WalletSession> _walletSessions = new();
    private readonly Dictionary<Guid, TimeSpan> _loginAllowed = new();
    private readonly Dictionary<Guid, TransferQuote> _transfers = new();
    private sealed class DepositJob(Guid id, ICommonSession session, EntityUid pda, int slot, int profileId, string asset,
        long units, EntityUid coin, EntityUid escrow, RecoveryRemoval? removal, string walletAddress, Guid walletVersion)
    {
        public readonly string WalletAddress = walletAddress;
        public readonly Guid WalletVersion = walletVersion;
        public readonly Guid Id = id;
        public readonly ICommonSession Session = session;
        public readonly EntityUid Pda = pda;
        public readonly int Slot = slot;
        public readonly int ProfileId = profileId;
        public readonly string Asset = asset;
        public readonly long Units = units;
        public readonly EntityUid Coin = coin;
        public readonly EntityUid Escrow = escrow;
        public readonly RecoveryRemoval? Removal = removal;
        public bool Busy;
    }
    private readonly Dictionary<Guid, Quote> _quotes = new();
    private readonly HashSet<Guid> _busy = new();
    private readonly Dictionary<Guid, TimeSpan> _nextRequest = new();
    private readonly Dictionary<Guid, TimeSpan> _nextMutation = new();
    private readonly Dictionary<Guid, DepositJob> _deposits = new();
    private TimeSpan _nextRetry;
    public bool Enabled => _config.GetCVar(ExchangeCVars.Enabled);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public override void Initialize()
    {
        SubscribeNetworkEvent<ExchangeRequest>(OnRequest);
        SubscribeLocalEvent<CartridgeLoaderComponent, CartridgeLoaderPreinstallEvent>(OnLoaderInit);
        InitializeMarketFeeds();
    }

    private void OnLoaderInit(EntityUid uid, CartridgeLoaderComponent loader, CartridgeLoaderPreinstallEvent args)
    {
        if (HasComp<PdaComponent>(uid) && !loader.PreinstalledPrograms.Contains("DLExchangeCartridge"))
            loader.PreinstalledPrograms = [..loader.PreinstalledPrograms, "DLExchangeCartridge"];
    }

    public bool CanUse(ICommonSession session, EntityUid pda)
        => Exists(pda) && !EntityManager.IsQueuedForDeletion(pda) && session.AttachedEntity is { } actor &&
            HasComp<PdaComponent>(pda) && Transform(pda).ParentUid == actor &&
            _ui.IsUiOpen(pda, PdaUiKey.Key, actor) && TryComp<CartridgeLoaderComponent>(pda, out var loader) &&
            loader.ActiveProgram is { } program && HasComp<ExchangeCartridgeComponent>(program) &&
            TryComp<MobStateComponent>(actor, out var state) && state.CurrentState == MobState.Alive;

    private async void OnRequest(ExchangeRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var user = session.UserId.UserId;
        if (!TryGetEntity(request.Pda, out var pda) || pda is not { } device || !CanUse(session, device) ||
            request.Units is < 1 or > ExchangeMarket.MaxOrderUnits || request.PeriodHours is not (1 or 6 or 24 or 168) ||
            !ExchangeMarket.Assets.Any(a => a.Id == request.Asset) || !_busy.Add(user)) return;
        var payment = false;
        try
        {
            var limiter = request.Action == ExchangeAction.Refresh ? _nextRequest : _nextMutation;
            if (limiter.GetValueOrDefault(user) > _timing.RealTime) return;
            limiter[user] = _timing.RealTime + TimeSpan.FromSeconds(request.Action == ExchangeAction.Refresh ? .5 : .25);
            if (!Enabled) { SendDisabled(session, device); return; }
            if (!_bank.TryGetCharacter(session, out _, out _, out var slot)) return;
            if (_cashSales.ContainsKey(user)) throw new InvalidOperationException("cash_pending");
            var access = _walletSessions.GetValueOrDefault(user);
            if (access != null && (access.Pda != device || access.Slot != slot || access.Expires < _timing.RealTime))
            { _walletSessions.Remove(user); access = null; }
            if (request.Action is ExchangeAction.Login or ExchangeAction.CreateWallet or ExchangeAction.Logout)
            {
                if (request.Action == ExchangeAction.Logout) { _walletSessions.Remove(user); _transfers.Remove(user); }
                else
                {
                    if (_loginAllowed.GetValueOrDefault(user) > _timing.RealTime) throw new InvalidOperationException("login_wait");
                    _loginAllowed[user] = _timing.RealTime + TimeSpan.FromSeconds(2);
                    if (request.Action == ExchangeAction.CreateWallet && access == null) throw new InvalidOperationException("wallet_locked");
                    if (request.Recipient == null || request.Recipient.Length > 6) throw new InvalidOperationException("bad_credentials");
                    var identity = await _database.ReadExchangeAsync(session.UserId, slot, Now);
                    var opened = await _database.LoginExchangeWalletAsync(session.UserId, slot,
                        new(request.Recipient, request.Password, identity.ProfileId, request.Action == ExchangeAction.CreateWallet));
                    _walletSessions[user] = new(device, slot, identity.ProfileId, opened.Address, _timing.RealTime + TimeSpan.FromMinutes(15), opened.Version);
                    _transfers.Remove(user);
                }
                var logged = _walletSessions.GetValueOrDefault(user);
                var state = await _database.ReadExchangeAsync(session.UserId, slot, Now, walletAddress: logged?.Address ?? "");
                Publish(session, device, state, request.Asset, request.Units, request.PeriodHours, "");
                return;
            }
            if (access != null) _walletSessions[user] = access with { Expires = _timing.RealTime + TimeSpan.FromMinutes(15) };
            if (request.Action != ExchangeAction.Refresh && access == null) throw new InvalidOperationException("wallet_locked");

            if (request.Action == ExchangeAction.ChangePassword)
            {
                if (_loginAllowed.GetValueOrDefault(user) > _timing.RealTime) throw new InvalidOperationException("login_wait");
                _loginAllowed[user] = _timing.RealTime + TimeSpan.FromSeconds(2);
                await _database.ChangeExchangePasswordAsync(session.UserId, slot,
                    new(access!.Address, access.Profile, request.Password, request.NewPassword));
                // Every session, including this one, must authenticate against the new password.
                foreach (var key in _walletSessions.Where(p => p.Value.Address == access.Address).Select(p => p.Key).ToArray())
                { _walletSessions.Remove(key); _quotes.Remove(key); _transfers.Remove(key); }
                var changed = await _database.ReadExchangeAsync(session.UserId, slot, Now, walletAddress: access.Address);
                Publish(session, device, changed, request.Asset, request.Units, request.PeriodHours, "password_changed");
                return;
            }

            if (request.Action is ExchangeAction.PreviewTransfer or ExchangeAction.Transfer)
            {
                if (request.Recipient == null || request.Recipient.Length > 6) throw new InvalidOperationException("wallet_missing");
                var address = request.Recipient.Trim().ToUpperInvariant();
                ExchangeResult transferResult;
                if (request.Action == ExchangeAction.PreviewTransfer)
                {
                    var recipient = await _database.FindExchangeRecipientAsync(address);
                    transferResult = await _database.ReadExchangeAsync(session.UserId, slot, Now, walletAddress: access!.Address);
                    if (recipient.Address == transferResult.Address) throw new InvalidOperationException("self_transfer");
                    if (transferResult.Wallet.GetValueOrDefault(request.Asset) < request.Units) throw new InvalidOperationException("insufficient_crypto");
                    _transfers[user] = new(Guid.NewGuid(), device, slot, transferResult.ProfileId, request.Asset, request.Units,
                        recipient, _timing.RealTime + TimeSpan.FromSeconds(30), access!.Address);
                }
                else
                {
                    if (!_transfers.Remove(user, out var transfer) || transfer.Id != request.Quote || transfer.Pda != device ||
                        transfer.Slot != slot || transfer.SenderAddress != access!.Address || transfer.Asset != request.Asset || transfer.Units != request.Units ||
                        transfer.Recipient.Address != address || transfer.Expires < _timing.RealTime)
                        throw new InvalidOperationException("quote_expired");
                    transferResult = await _database.TransferExchangeAsync(session.UserId,
                        new(transfer.Id, slot, transfer.Profile, address, transfer.Recipient.ProfileId, request.Asset, request.Units, transfer.SenderAddress, access!.Version), Now);
                }
                var display = _quotes.GetValueOrDefault(user);
                Publish(session, device, transferResult, display?.Asset ?? "contrib", display?.Units ?? 1000,
                    display?.Period ?? request.PeriodHours, request.Action == ExchangeAction.Transfer ? "transferred" : "");
                return;
            }
            if (request.Action == ExchangeAction.Deposit)
            {
                if (_deposits.Values.Any(d => d.Session.UserId == session.UserId))
                    throw new InvalidOperationException("deposit_pending");
                if (!_quotes.TryGetValue(user, out var depositQuote) || depositQuote.Pda != device ||
                    depositQuote.Slot != slot || depositQuote.Expires < _timing.RealTime)
                    throw new InvalidOperationException("quote_expired");
                var actor = session.AttachedEntity!.Value;
                var coin = _hands.EnumerateHeld(actor).FirstOrDefault(c => ExchangeMarket.Assets.Any(a => a.CoinPrototype == MetaData(c).EntityPrototype?.ID));
                if (!Exists(coin) || !TryComp<StackComponent>(coin, out var stack) || stack.Count <= 0)
                    throw new InvalidOperationException("hold_coins");
                var asset = ExchangeMarket.Assets.Single(a => a.CoinPrototype == MetaData(coin).EntityPrototype?.ID);
                var units = checked((long)stack.Count * ExchangeMarket.UnitsPerCoin);
                if (units > 1000000000 || _deposits.Count >= 256) throw new InvalidOperationException("busy");
                var removal = _recovery.ConsumedItems([coin]);
                var escrow = Spawn(null, MapCoordinates.Nullspace);
                var container = _containers.EnsureContainer<Container>(escrow, "exchange-coins");
                if (!_hands.TryDrop(actor, coin) || !_containers.Insert(coin, container))
                { QueueDel(escrow); throw new InvalidOperationException("hold_coins"); }
                var job = new DepositJob(Guid.NewGuid(), session, device, slot, depositQuote.ProfileId, asset.Id, units, coin, escrow, removal, access!.Address, access.Version);
                _deposits.Add(job.Id, job);
                await ProcessDeposit(job);
                return;
            }
            ExchangeResult result;
            if (request.Action is ExchangeAction.Buy or ExchangeAction.Sell)
            {
                if (!_quotes.Remove(user, out var quote) || quote.Id != request.Quote || quote.Pda != device ||
                    quote.Slot != slot || quote.WalletAddress != access!.Address || quote.Asset != request.Asset || quote.Units != request.Units || quote.Expires < _timing.RealTime)
                    throw new InvalidOperationException("quote_expired");
                if (!_bank.BeginExchangePayment(session.UserId)) throw new InvalidOperationException("busy");
                payment = true;
                var buy = request.Action == ExchangeAction.Buy;
                var actor = session.AttachedEntity!.Value;
                if (buy && !HasComp<BankAccountComponent>(actor)) throw new InvalidOperationException("no_bank");
                if (!buy && !HasComp<BankAccountComponent>(actor))
                {
                    await BeginCashSale(session, device, quote);
                    return;
                }
                result = await _database.TradeExchangeAsync(session.UserId, new(quote.Id, slot, quote.Asset,
                    quote.Units, buy, buy ? quote.BuyCost : quote.SellProceeds, quote.ProfileId, WalletAddress: quote.WalletAddress, WalletVersion: quote.WalletVersion), Now);
                if (result.Applied) PurchaseSound(device);
                await _bank.PublishExchangeBalance(session.UserId, slot);
            }
            else if (request.Action == ExchangeAction.Refresh)
                result = await _database.ReadExchangeAsync(session.UserId, slot, Now, walletAddress: access?.Address ?? "");
            else return;
            Publish(session, device, result, request.Asset, request.Units, request.PeriodHours, "");
        }
        catch (Exception e)
        {
            var status = e is InvalidOperationException && KnownErrors.Contains(e.Message) ? e.Message : "unavailable";
            if (CanUse(session, device)) RaiseNetworkEvent(new ExchangeSnapshot { Pda = request.Pda, At = Now, Status = status, Enabled = Enabled }, session);
            if (status == "unavailable") Log.Warning($"Exchange request failed ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}); receipts remain authoritative.");
        }
        finally
        {
            if (payment) _bank.EndExchangePayment(session.UserId);
            _busy.Remove(user);
        }
    }

    private static readonly HashSet<string> KnownErrors =
    ["invalid_order", "price_changed", "insufficient_spessos", "insufficient_crypto", "insufficient_liquidity",
        "quote_expired", "busy", "deposit_pending", "hold_coins", "character_changed", "wallet_missing", "self_transfer", "no_bank", "cash_pending", "recovery_unavailable", "wallet_locked", "bad_credentials", "password_length", "login_wait", "wallet_limit"];

    private void PurchaseSound(EntityUid device)
    {
        if (Exists(device)) _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg"), device,
            AudioParams.Default.WithMaxDistance(5f));
    }

    private void SendDisabled(ICommonSession session, EntityUid pda)
        => RaiseNetworkEvent(new ExchangeSnapshot { Pda = GetNetEntity(pda), At = Now, Status = "disabled" }, session);

    private void Publish(ICommonSession session, EntityUid pda, ExchangeResult result, string asset, long units, int period, string status)
    {
        if (!CanUse(session, pda) || !_bank.IsCharacter(session, result.Slot)) return;
        var quote = result.Market.Quote(asset, units);
        var token = Guid.NewGuid();
        _quotes[session.UserId.UserId] = new(token, pda, result.Slot, result.ProfileId, asset, units, quote.BuyCost, quote.SellProceeds,
            period, _timing.RealTime + TimeSpan.FromSeconds(30), result.Address, result.WalletVersion);
        var now = Now;
        ExchangePoint[] History(IEnumerable<ExchangeCandle> source)
        {
            var points = source.Where(p => p.At >= now - period * 3600000L).ToArray();
            if (points.Length <= 120) return points.Select(p => new ExchangePoint(p.At, (double)p.Price, p.Volume)).ToArray();
            var stride = (int)Math.Ceiling(points.Length / 119d);
            return points.Select((p, i) => (Point: p, Group: i / stride)).GroupBy(p => p.Group)
                .Select(g => new ExchangePoint(g.Last().Point.At, (double)g.Last().Point.Price, g.Sum(p => p.Point.Volume))).ToArray();
        }
        var access = _walletSessions.GetValueOrDefault(session.UserId.UserId);
        var unlocked = access != null && access.Pda == pda && access.Slot == result.Slot && access.Profile == result.ProfileId &&
            access.Address == result.Address && access.Version == result.WalletVersion && access.Expires >= _timing.RealTime;
        if (!unlocked) _walletSessions.Remove(session.UserId.UserId);
        var transfer = unlocked ? _transfers.GetValueOrDefault(session.UserId.UserId) : null;
        if (transfer != null && (transfer.Profile != result.ProfileId || transfer.Slot != result.Slot ||
                                transfer.Pda != pda || transfer.Expires < _timing.RealTime)) transfer = null;
        RaiseNetworkEvent(new ExchangeSnapshot
        {
            Pda = GetNetEntity(pda), Enabled = Enabled, At = now, PeriodHours = period, Bank = result.Bank, Status = status,
            WalletUnlocked = unlocked, NeedsPassword = result.NeedsPassword, WalletCharacter = result.WalletCharacter,
            Wallets = result.Wallets.Select(w => new ExchangeWalletView(w.Address, w.Name, w.NeedsPassword)).ToArray(),
            Withdrawals = unlocked ? result.Withdrawals.Select(w => new ExchangeWithdrawalView(w.At, w.Amount,
                w.AccountOwner, w.Cash, w.Asset, w.Units)).ToArray() : [],
            HasBank = session.AttachedEntity is { } actor && HasComp<BankAccountComponent>(actor), Address = result.Address,
            TransferQuote = transfer?.Id ?? Guid.Empty, TransferAsset = transfer?.Asset ?? "", TransferUnits = transfer?.Units ?? 0,
            RecipientAddress = transfer?.Recipient.Address ?? "", RecipientCharacter = transfer?.Recipient.Character ?? "",
            SelectedAsset = asset, Units = units, Quote = token, BuyCost = quote.BuyCost,
            SellProceeds = quote.SellProceeds, CanSell = quote.CanSell,
            Assets = ExchangeMarket.Assets.Select(a => new ExchangeAssetView(a.Id, a.Symbol,
                (double)result.Market.Spot(a.Id), unlocked ? result.Wallet.GetValueOrDefault(a.Id) : 0, result.Market.Pools[a.Id].Cash,
                History(result.Market.Pools[a.Id].History))).ToArray(),
            Materials = result.Market.Materials.Select(m => new ExchangeMaterialView(m.Key,
                (double)result.Market.MaterialBase.GetValueOrDefault(m.Key), (double)(m.Value.LastOrDefault()?.Price ?? 0),
                History(m.Value))).ToArray(),
        }, session);
    }

    private async Task ProcessDeposit(DepositJob job)
    {
        if (job.Busy) return;
        job.Busy = true;
        try
        {
            var result = await _database.DepositExchangeAsync(job.Session.UserId, job.Slot, job.Asset, job.Units,
                job.Id, job.Removal, Now, expectedProfileId: job.ProfileId, walletAddress: job.WalletAddress, walletVersion: job.WalletVersion);
            // The asset-removal receipt and wallet credit commit together. Never return escrow on an ambiguous DB error.
            if (Exists(job.Coin)) Del(job.Coin);
            if (Exists(job.Escrow)) QueueDel(job.Escrow);
            _deposits.Remove(job.Id);
            var display = _quotes.GetValueOrDefault(job.Session.UserId.UserId);
            Publish(job.Session, job.Pda, result, display?.Asset ?? job.Asset, display?.Units ?? 1000, display?.Period ?? 24, "deposited");
        }
        catch (InvalidOperationException e) when (e.Message is "character_changed" or "wallet_locked" &&
            job.Session.AttachedEntity is { } actor && Exists(actor))
        {
            // This explicit pre-commit rejection has no receipt. Ambiguous I/O failures take the retry path below.
            if (Exists(job.Coin))
            {
                _containers.TryRemoveFromContainer(job.Coin, force: true);
                _transform.DropNextTo(job.Coin, actor);
            }
            if (Exists(job.Escrow)) QueueDel(job.Escrow);
            _deposits.Remove(job.Id);
        }
        catch (Exception e)
        {
            Log.Warning($"Crypto deposit is held in escrow for retry ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}).");
            if (CanUse(job.Session, job.Pda)) RaiseNetworkEvent(new ExchangeSnapshot
                { Pda = GetNetEntity(job.Pda), Enabled = true, At = Now, Status = "deposit_pending" }, job.Session);
        }
        finally { job.Busy = false; }
    }

    public override void Update(float frameTime)
    {
        UpdateMarketFeeds();
        if (_timing.RealTime < _nextRetry) return;
        _nextRetry = _timing.RealTime + TimeSpan.FromSeconds(5);
        foreach (var job in _deposits.Values.Where(d => !d.Busy).Take(8).ToArray()) _ = ProcessDeposit(job);
        foreach (var job in _cashSales.Values.Where(d => !d.Busy).Take(8).ToArray()) _ = ProcessCashSale(job);
        foreach (var key in _transfers.Where(p => p.Value.Expires < _timing.RealTime).Select(p => p.Key).ToArray()) _transfers.Remove(key);
        foreach (var key in _quotes.Where(p => p.Value.Expires < _timing.RealTime).Select(p => p.Key).ToArray()) _quotes.Remove(key);
        foreach (var key in _nextRequest.Where(p => p.Value < _timing.RealTime - TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) _nextRequest.Remove(key);
        foreach (var key in _nextMutation.Where(p => p.Value < _timing.RealTime - TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) _nextMutation.Remove(key);
    }
}
