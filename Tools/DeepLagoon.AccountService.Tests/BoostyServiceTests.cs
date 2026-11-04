using System.Diagnostics;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    [Test]
    public async Task BoostyWorkerPaysRealAccountServiceExactlyOnce()
    {
        var worker = Environment.GetEnvironmentVariable("ACCOUNT_TEST_BOOSTY_PATH");
        if (worker == null || !File.Exists(Path.Combine(worker, "boosty_service.py")))
            Assert.Ignore("Set ACCOUNT_TEST_BOOSTY_PATH to the Boosty service directory for the actual-worker test.");
        LinkReviewer();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ACCOUNT_TEST_PYTHON") ?? "python")
        {
            WorkingDirectory = worker!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "boosty_contract_probe.py"));
        start.ArgumentList.Add(_http.BaseAddress!.ToString().TrimEnd('/'));
        start.ArgumentList.Add(_directory);
        start.Environment["PYTHONPATH"] = worker!;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.That(process.ExitCode, Is.Zero, await error + await output);
        await using var db = _settings.OpenDatabase();
        var rows = await db.LagoonCoinOperations.Where(p => p.UserId == _user && p.OperationId.StartsWith("payment:boosty:event:")).ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Amount, Is.EqualTo(250));
        Assert.That((await Economy(_user)).Balance, Is.EqualTo(250));
        Assert.That(await db.Preference.Where(p => p.UserId == _user).Select(p => p.Money).SingleAsync(), Is.EqualTo(777));
        TestContext.Out.WriteLine(await output);
    }
}
