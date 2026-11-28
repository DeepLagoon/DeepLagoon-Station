using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Cargo.Components;
using Content.Server.Stack;
using Content.Shared._NF.Bank.BUI;
using Content.Shared.Cargo;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Map;

namespace Content.Server._DeepLagoon.Voyages;
public sealed partial class VoyageSystem
{
    private sealed class LaunchWork
    {
        public required VoyageJob Job;
        public Order? Order;
        public required EntityUid Escrow;
        public readonly List<(EntityUid Entity, EntityCoordinates Coordinates, BaseContainer? Container)> Goods = new();
        public RecoveryRemoval? Removal;
        public long Reserved;
        public bool Taken, Busy;
    }
    private readonly Dictionary<Guid, LaunchWork> _launches = new();
    private async Task<bool> Launch(EntityUid console, VoyageJob job, Order? order)
        => await CommitLaunch(PrepareLaunch(console, job, order));
    private LaunchWork PrepareLaunch(EntityUid console, VoyageJob job, Order? order, bool standalone = true)
    {
        if (_launches.Count >= 32 || _jobs.Values.Count(j => !j.Paid) >= 128) throw new InvalidOperationException("Слишком много рейсов");
        if (_launches.ContainsKey(job.Id)) throw new InvalidOperationException("Груз уже обрабатывается");
        var plan = new Dictionary<EntityUid, int>();
        if (order != null && !_cargo.PlanVoyageCargo(console, order.Prototype, out plan))
            throw new InvalidOperationException("На продающих поддонах недостаточно подходящего груза");
        var work = new LaunchWork { Job = job, Order = order, Escrow = Spawn(null, MapCoordinates.Nullspace) };
        try
        {
            if (order != null)
            {
                var needed = Math.Max(0, job.Reward - Math.Max(0, order.Data.FundedReward));
                if (needed > 0)
                {
                    if (!_bank.TrySectorWithdraw(order.Payer, needed, LedgerEntryType.StationWithdrawalBounty))
                        throw new InvalidOperationException("Бюджета заказчика недостаточно с учётом резерва зарплат");
                    work.Reserved = needed;
                }
                if (!_cargo.TakeVoyageBounty(order.Station, order.Data)) throw new InvalidOperationException("Заказ уже принят другим перевозчиком");
                work.Taken = true;
            }
            var container = _containers.EnsureContainer<Container>(work.Escrow, "voyage-cargo");
            foreach (var (source, amount) in plan)
            {
                var entity = source;
                if (TryComp<StackComponent>(source, out var stack) && amount < stack.Count)
                    entity = EntityManager.System<StackSystem>().Split(source, amount, Transform(source).Coordinates, stack)
                        ?? throw new InvalidOperationException("Не удалось отделить нужное количество материала");
                var coordinates = Transform(entity).Coordinates;
                _containers.TryGetContainingContainer((entity, null, null), out var old);
                work.Goods.Add((entity, coordinates, old));
                if (!_containers.Insert(entity, container)) throw new InvalidOperationException("Груз невозможно погрузить");
                EntityManager.System<MetaDataSystem>().SetEntityPaused(entity, true);
            }
            var consumed = new HashSet<EntityUid>();
            void Children(EntityUid uid)
            {
                if (!consumed.Add(uid) || consumed.Count > 4096) return;
                if (TryComp<ContainerManagerComponent>(uid, out var cm))
                    foreach (var nested in cm.Containers.Values.SelectMany(c => c.ContainedEntities)) Children(nested);
            }
            foreach (var item in work.Goods) Children(item.Entity);
            if (consumed.Count > 4096) throw new InvalidOperationException("Слишком сложный груз");
            work.Removal = _recovery.ConsumedItems(consumed);
            if (standalone) _launches.Add(job.Id, work);
        }
        catch { Rollback(work); throw; }
        return work;
    }
    private async Task RetryLaunch(LaunchWork work)
    {
        try { await CommitLaunch(work); }
        catch (Exception e) { Log.Warning($"Voyage {work.Job.Id} cancelled before departure: {e.Message}"); }
    }
    private async Task<bool> CommitLaunch(LaunchWork work)
    {
        if (work.Busy) return false; work.Busy = true;
        var committed = false;
        try
        {
            await _db.CommitVoyageAsync(work.Job, work.Removal);
            committed = true;
            _jobs.TryAdd(work.Job.Id, work.Job);
            if (work.Order is { } order) _bounties[work.Job.Id] = (order.Station, order.Data);
            // The durable manifest now owns these units; crash recovery removes them in the same DB transaction.
            QueueDel(work.Escrow);
            _launches.Remove(work.Job.Id);
            Log.Info($"Voyage {work.Job.Id}: loaded {work.Job.Manifest}, owner={work.Job.User}, profile={work.Job.Profile}, payer={work.Job.Account}, reward escrow={work.Job.Reward}, origin={work.Job.Origin}, destination={work.Job.Destination}");
            return true;
        }
        catch (InvalidOperationException) when (!committed) { _launches.Remove(work.Job.Id); Rollback(work); throw; }
        catch (Exception e) { Log.Warning($"Voyage {work.Job.Id}: cargo locked until dispatch receipt is confirmed: {e.Message}"); }
        finally { work.Busy = false; }
        return false;
    }
    private void Rollback(LaunchWork work)
    {
        foreach (var (entity, coordinates, old) in work.Goods)
        {
            if (!Exists(entity)) continue;
            EntityManager.System<MetaDataSystem>().SetEntityPaused(entity, false);
            if (old != null && Exists(old.Owner) && _containers.Insert(entity, old)) continue;
            if (_containers.TryGetContainingContainer((entity, null, null), out var escrow))
                _containers.Remove((entity, null, null), escrow, force: true, destination: coordinates);
            else _transform.SetCoordinates(entity, coordinates);
        }
        if (work.Order is { } order)
        {
            if (work.Taken) _cargo.RestoreVoyageBounty(order.Station, order.Data);
            if (work.Reserved > 0 && work.Job.Cycle == _world.Cycle && !_world.Closed) _bank.TrySectorDeposit(order.Payer, work.Reserved, LedgerEntryType.StationDepositOther);
        }
        QueueDel(work.Escrow);
    }
}
