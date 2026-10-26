using Robust.Shared.Serialization;
namespace Content.Shared._DeepLagoon.Currency;

[Serializable, NetSerializable]
public sealed class LagoonEconomyRequest(Guid request) : EntityEventArgs
{
    public readonly Guid Request = request;
}
[Serializable, NetSerializable]
public sealed class LagoonEconomyAction(Guid token, string action, string product, Guid order) : EntityEventArgs
{
    public readonly Guid Token = token, Order = order;
    public readonly string Action = action, Product = product;
}
[Serializable, NetSerializable]
public sealed class LagoonEconomyStateEvent(Guid token, string data, Guid request) : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly Guid Token = token;
    public readonly string Data = data;
}
[Serializable, NetSerializable]
public sealed class LagoonPaidSupportersRequest : EntityEventArgs;
[Serializable, NetSerializable]
public sealed class LagoonPaidSupportersEvent(string[] names) : EntityEventArgs
{
    public readonly string[] Names = names;
}
