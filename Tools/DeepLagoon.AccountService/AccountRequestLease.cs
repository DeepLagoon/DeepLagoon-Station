namespace DeepLagoon.AccountService;

internal sealed class AccountRequestLease(SemaphoreSlim slots) : IDisposable
{
    public bool Acquired { get; } = slots.Wait(0);
    public void Dispose() { if (Acquired) slots.Release(); }
}
