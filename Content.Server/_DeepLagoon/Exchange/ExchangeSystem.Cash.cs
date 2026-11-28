using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Exchange;

public sealed partial class ExchangeSystem
{
    private sealed class CashSaleJob(ICommonSession session, EntityUid actor, EntityUid pda, EntityUid cash,
        EntityUid escrow, ExchangeTradeRequest request, int period)
    {
        public readonly ICommonSession Session = session;
        public readonly EntityUid Actor = actor, Pda = pda, Cash = cash, Escrow = escrow;
        public readonly ExchangeTradeRequest Request = request;
        public readonly int Period = period;
        public ExchangeResult? Result;
        public bool Busy;
    }
    private readonly Dictionary<Guid, CashSaleJob> _cashSales = new();

    private async Task BeginCashSale(ICommonSession session, EntityUid pda, Quote quote)
    {
        if (quote.SellProceeds is <= 0 or > int.MaxValue) throw new InvalidOperationException("invalid_order");
        var actor = session.AttachedEntity!.Value;
        var escrow = Spawn(null, MapCoordinates.Nullspace);
        var cash = Spawn("SpaceCash", MapCoordinates.Nullspace);
        try
        {
            EntityManager.System<Content.Server.Stack.StackSystem>().SetCount(cash, (int)quote.SellProceeds);
            _containers.Insert(cash, _containers.EnsureContainer<Container>(escrow, "crypto-cash"));
            var capture = _recovery.CaptureExternalDelivery(session.UserId.UserId, quote.ProfileId, [cash]);
            var request = new ExchangeTradeRequest(quote.Id, quote.Slot, quote.Asset, quote.Units, false,
                quote.SellProceeds, quote.ProfileId, true, capture, quote.WalletAddress, quote.WalletVersion);
            var job = new CashSaleJob(session, actor, pda, cash, escrow, request, quote.Period);
            _cashSales.Add(session.UserId.UserId, job);
            await ProcessCashSale(job);
        }
        catch
        {
            // ProcessCashSale handles ambiguous outcomes internally; this branch precedes its DB write.
            if (Exists(escrow)) QueueDel(escrow);
            if (Exists(cash)) QueueDel(cash);
            throw;
        }
    }

    private async Task ProcessCashSale(CashSaleJob job)
    {
        if (job.Busy) return;
        job.Busy = true;
        try
        {
            job.Result ??= await _database.TradeExchangeAsync(job.Session.UserId, job.Request, Now);
            var actor = job.Actor;
            if (!Exists(actor) || EntityManager.IsQueuedForDeletion(actor))
            {
                if (job.Session.AttachedEntity is not { } current || !Exists(current) ||
                    !_bank.IsCharacter(job.Session, job.Request.Slot))
                    return;
                var identity = await _database.ReadExchangeAsync(job.Session.UserId, job.Request.Slot, Now);
                if (identity.ProfileId != job.Request.ExpectedProfileId) return;
                actor = current;
            }
            _recovery.AcceptExternalDelivery(job.Request.CashRecovery, [job.Cash]);
            _containers.TryRemoveFromContainer(job.Cash, force: true);
            _transform.DropNextTo(job.Cash, actor);
            _hands.TryPickupAnyHand(actor, job.Cash, animateUser: false);
            _cashSales.Remove(job.Session.UserId.UserId);
            QueueDel(job.Escrow);
            PurchaseSound(Exists(job.Pda) ? job.Pda : actor);
            Publish(job.Session, job.Pda, job.Result, job.Request.Asset, job.Request.Units, job.Period, "cash_paid");
        }
        catch (InvalidOperationException e) when (job.Result == null && KnownErrors.Contains(e.Message))
        {
            _cashSales.Remove(job.Session.UserId.UserId);
            QueueDel(job.Escrow);
            if (Exists(job.Cash)) QueueDel(job.Cash);
            if (CanUse(job.Session, job.Pda)) RaiseNetworkEvent(new Content.Shared._DeepLagoon.Exchange.ExchangeSnapshot
                { Pda = GetNetEntity(job.Pda), At = Now, Enabled = Enabled, Status = e.Message }, job.Session);
        }
        catch (Exception e)
        {
            Log.Warning($"Crypto cash sale awaits receipt confirmation ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}).");
        }
        finally { job.Busy = false; }
    }
}
