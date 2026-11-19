using System.Linq;
using Content.Server.Database;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    private sealed class Delivery(RecoveryPayment payment)
    {
        public RecoveryPayment Payment = payment;
        public int Next;
    }
    private readonly Dictionary<object, Delivery> _purchaseDeliveries = new(ReferenceEqualityComparer.Instance);
    private RecoveryPayment? _spawnPayment;
    private sealed class SpawnScope(CrashRecoverySystem owner, RecoveryPayment? previous) : IDisposable
    {
        public void Dispose() => owner._spawnPayment = previous;
    }

    public RecoveryPayment? BeginProfilePurchase(Guid user, int total)
        => _open && total > 0 ? new(_scope, _run, [Guid.NewGuid()], total, user, 0) : null;

    public IDisposable SpawnPurchaseScope(RecoveryPayment? payment)
    {
        var scope = new SpawnScope(this, _spawnPayment);
        _spawnPayment = payment;
        return scope;
    }

    private void AttributeSpawnedItem(EntityUid uid)
    {
        if (!_open || _spawnPayment is not { } payment) return;
        var identity = EnsureComp<RecoveryIdentityComponent>(uid).Identity;
        var state = new RecoveryAsset(identity, ++_revision, payment.User, payment.Profile, ++_nextYaml,
            MetaData(uid).EntityPrototype?.ID ?? "", RecoveryLocation.Loose, null, [], "", Purchase: payment.Products[0]);
        _assets[uid] = state;
        _tracked.Add(uid);
        _entitiesByIdentity[identity] = uid;
        _serializer?.YamlUidMap.TryAdd(uid, state.YamlId);
        Stage(state);
        _discover.Enqueue(uid, 65536);
    }

    public RecoveryRemoval? ConsumedItems(IEnumerable<EntityUid> items)
    {
        if (!_open) return null;
        return new(_scope, _run, items.Select(uid => EnsureComp<RecoveryIdentityComponent>(uid).Identity).ToArray());
    }

    public RecoveryPayment? BeginPurchase(EntityUid buyer, int total, int quantity = 1, int[]? unitPrices = null)
    {
        if (!_open || _closing || total <= 0 || quantity is < 1 or > 8192 || !TryComp<RecoveryBodyComponent>(buyer, out var body)) return null;
        return new(_scope, _run, Enumerable.Range(0, quantity).Select(_ => Guid.NewGuid()).ToArray(), total, body.User, body.Profile, unitPrices);
    }

    public void AttachPurchase(object deliveryKey, RecoveryPayment? payment, bool cashOnly = false)
    {
        if (payment == null || !_open) return;
        _purchaseDeliveries[deliveryKey] = new(payment);
        if (!cashOnly) return; // bank receipts already committed atomically with the debit
        for (var i = 0; i < payment.Products.Length; i++)
        {
            var amount = payment.UnitPrices?[i] ?? payment.Total / payment.Products.Length + (i < payment.Total % payment.Products.Length ? 1 : 0);
            if (amount > 0) _purchases[payment.Products[i]] = new(payment.Products[i], payment.User, payment.Profile, amount, [payment.Products[i]]);
        }
    }

    public void BindPurchasedProduct(object deliveryKey, EntityUid product)
    {
        if (!_open || !_purchaseDeliveries.TryGetValue(deliveryKey, out var delivery) || delivery.Next >= delivery.Payment.Products.Length) return;
        var payment = delivery.Payment;
        var identity = payment.Products[delivery.Next++];
        EnsureComp<RecoveryIdentityComponent>(product).Identity = identity;
        _entitiesByIdentity[identity] = product;
        var state = new RecoveryAsset(identity, ++_revision, payment.User, payment.Profile, ++_nextYaml,
            MetaData(product).EntityPrototype?.ID ?? "", RecoveryLocation.Loose, null, [], "", Purchase: identity);
        _assets[product] = state;
        _tracked.Add(product);
        _serializer?.YamlUidMap.TryAdd(product, state.YamlId);
        Stage(state);
        _discover.Enqueue(product, 65536);
        if (delivery.Next >= payment.Products.Length) _purchaseDeliveries.Remove(deliveryKey);
    }

    public void BindPurchasedShip(RecoveryPayment? payment, EntityUid ship)
    {
        if (!_open || payment == null) return;
        var identity = payment.Products[0];
        EnsureComp<RecoveryIdentityComponent>(ship).Identity = identity;
        var state = new RecoveryAsset(identity, ++_revision, payment.User, payment.Profile, 0, "",
            RecoveryLocation.Loose, null, [], "", "ship", Purchase: identity);
        _assets[ship] = state;
        Stage(state);
    }

    /// <summary>After a durable apartment/parking save, recovery must defer to that store.</summary>
    public void Persisted(IEnumerable<EntityUid> entities)
    {
        if (!_open) return;
        foreach (var uid in entities)
            if (_assets.TryGetValue(uid, out var old))
            {
                var update = old with { Revision = ++_revision, Location = RecoveryLocation.Persistent, Data = "" };
                _assets[uid] = update;
                Stage(update);
            }
    }
}
