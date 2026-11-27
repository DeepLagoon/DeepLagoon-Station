using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Content.Server._DeepLagoon.Persistence;

/// <summary>One ordered SQLite writer. Receipts complete after the transaction, never on enqueue.</summary>
public sealed class SerialPersistenceQueue : IDisposable
{
    private interface IWork { void Execute(); }
    private sealed class Work<T>(Func<T> operation) : IWork
    {
        public readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute()
        {
            try { Completion.SetResult(operation()); }
            catch (Exception error) { Completion.SetException(error); }
        }
    }

    private readonly Channel<IWork> _work = Channel.CreateBounded<IWork>(new BoundedChannelOptions(1024)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private int _disposed;
    [ThreadStatic] private static SerialPersistenceQueue? _executing;
    public bool IsExecuting => ReferenceEquals(_executing, this);
    public int PendingCount => _work.Reader.Count;

    public SerialPersistenceQueue() => _worker = Task.Run(async () =>
    {
        await foreach (var item in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _executing = this;
            try { item.Execute(); }
            finally { _executing = null; }
        }
    });

    public Task<T> Enqueue<T>(Func<T> operation)
    {
        var item = new Work<T>(operation);
        if (Volatile.Read(ref _disposed) != 0 || !_work.Writer.TryWrite(item))
            return Task.FromException<T>(new InvalidOperationException("Persistence writer is closed or its queue is full; operation was not accepted."));
        return item.Completion.Task;
    }

    public Task Enqueue(Action operation) => Enqueue(() => { operation(); return true; });
    public Task FlushAsync() => Enqueue(static () => true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _work.Writer.TryComplete();
        _worker.GetAwaiter().GetResult();
    }
}
