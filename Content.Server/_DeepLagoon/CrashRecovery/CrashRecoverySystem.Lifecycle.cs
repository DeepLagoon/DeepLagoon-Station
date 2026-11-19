using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Shared.CCVar;
using Robust.Server.ServerStatus;
using Robust.Shared.Console;
using Robust.Shared.Asynchronous;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    [Dependency] private IStatusHost _status = default!;
    [Dependency] private IConsoleHost _console = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private sealed record SignalTarget(string Scope, Guid Run, Task Boot);
    private volatile SignalTarget? _signalTarget;
    private volatile bool _signalClosing;
    private PosixSignalRegistration? _term;
    private PosixSignalRegistration? _interrupt;

    private void InitializeLifecycle()
    {
        _console.AnyCommandExecuted += OnConsoleCommand;
        _status.AddHandler(HandleLifecycle);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => CloseOnNormalSignal());
            _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => CloseOnNormalSignal());
        }
        Console.CancelKeyPress += OnCancelKey;
    }

    private void OnCancelKey(object? sender, ConsoleCancelEventArgs args) => CloseOnNormalSignal();

    private void CloseOnNormalSignal()
    {
        var target = _signalTarget;
        if (target == null) return;
        Task? writer;
        lock (_writerGate)
        {
            _signalClosing = true;
            writer = _writer;
        }
        // This callback runs on the shutdown/signal thread, never reads or mutates ECS data,
        // and completes before allowing the operating system's normal termination to proceed.
        try
        {
            async Task Fence()
            {
                try { await target.Boot.ConfigureAwait(false); } catch { /* no run may have opened */ }
                if (writer != null) try { await writer.ConfigureAwait(false); } catch { /* close still invalidates the run */ }
                await _db.CloseCrashRecovery(target.Scope, target.Run).ConfigureAwait(false);
            }
            Fence().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch (Exception error) { Log.Error($"Normal-signal recovery fence failed: {error.GetType().Name}"); }
    }

    private void OnConsoleCommand(IConsoleShell shell, string name, string command, string[] args)
    {
        if (name is "shutdown" or "quit")
            _tasks.BlockWaitOnTask(CloseNormally());
    }

    private async Task<bool> HandleLifecycle(IStatusHandlerContext context)
    {
        if (context.Url.AbsolutePath != "/deeplagoon/crash-recovery/close") return false;
        context.ResponseHeaders["Cache-Control"] = "no-store";
        var peer = context.RemoteEndPoint.Address;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        var expected = "Bearer " + _cfg.GetCVar(CCVars.AdminApiToken);
        if (!IPAddress.IsLoopback(peer) || expected.Length < 39 ||
            !context.RequestHeaders.TryGetValue("Authorization", out var auth) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(auth.ToString())))
        {
            await context.RespondErrorAsync(HttpStatusCode.Unauthorized);
            return true;
        }
        if (context.RequestMethod != HttpMethod.Post)
        {
            await context.RespondErrorAsync(HttpStatusCode.MethodNotAllowed);
            return true;
        }
        var completion = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => completion.TrySetResult(CloseNormally()));
        try
        {
            await (await completion.Task).WaitAsync(TimeSpan.FromSeconds(15));
            await context.RespondNoContentAsync();
        }
        catch (Exception e)
        {
            Log.Error($"Recovery shutdown fence failed: {e.GetType().Name}");
            await context.RespondErrorAsync(HttpStatusCode.ServiceUnavailable);
        }
        return true;
    }

    public override void Shutdown()
    {
        _tasks.BlockWaitOnTask(StopWorker());
        _console.AnyCommandExecuted -= OnConsoleCommand;
        EntityManager.EntityInitialized -= OnEntityInitialized;
        _cfg.UnsubValueChanged(RecoveryCVars.Enabled, ConfigureEnabled);
        Console.CancelKeyPress -= OnCancelKey;
        _term?.Dispose(); _interrupt?.Dispose();
        // Never mark clean here: fatal engine exits can invoke EntitySystem.Shutdown as well.
        base.Shutdown();
    }
}
