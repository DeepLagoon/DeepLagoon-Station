using System.Diagnostics;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    [Test]
    public async Task ActualBotAdaptersAndDurableQueueAgreeWithAccountService()
    {
        var bot = Environment.GetEnvironmentVariable("ACCOUNT_TEST_BOT_PATH");
        if (bot == null || !File.Exists(Path.Combine(bot, "economy_api.py")))
            Assert.Ignore("Set ACCOUNT_TEST_BOT_PATH to the companion Bot checkout for the cross-repository test.");
        LinkReviewer();
        await Fund(1000);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ACCOUNT_TEST_PYTHON") ?? "python")
        {
            WorkingDirectory = bot!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "bot_contract_probe.py"));
        start.ArgumentList.Add(_http.BaseAddress!.ToString().TrimEnd('/'));
        start.ArgumentList.Add(_directory);
        start.Environment["PYTHONPATH"] = bot!;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.That(process.ExitCode, Is.Zero, await error + await output);
        Assert.That((await Economy(_user)).Balance, Is.EqualTo(850));
        TestContext.Out.WriteLine(await output);
    }
}
