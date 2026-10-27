#nullable enable
using System;
using System.IO;
using System.Reflection;
using Content.Client.UserInterface.Systems.Chat.Controls;
using Moq;
using NUnit.Framework;
using Robust.Shared.ContentPack;
using Robust.Shared.Log;

namespace Content.Tests.Client;

[TestFixture]
public sealed class ClientSandboxTest
{
    [TestCase(false)]
    [TestCase(true)]
    public void ClientModulesPassEngineTypeChecks(bool shared) => CheckModule(shared);

    [Test]
    public void ClientModulePassesInstalledLauncherEngineTypeChecks()
    {
        var engineDirectory = Environment.GetEnvironmentVariable("SS14_LAUNCHER_ENGINE_DIR");
        if (string.IsNullOrEmpty(engineDirectory))
            Assert.Ignore("Set SS14_LAUNCHER_ENGINE_DIR to test against the installed launcher's engine DLLs.");
        Assert.That(File.Exists(Path.Combine(engineDirectory!, "Robust.Client.dll")), Is.True);
        CheckModule(false, engineDirectory);
    }

    private static void CheckModule(bool shared, string? engineDirectory = null)
    {
        var log = new LogManager();
        var handler = new Mock<ILogHandler>();
        log.RootSawmill.AddHandler(handler.Object);
        var resources = new Mock<IResourceManager>();
        resources.Setup(r => r.ContentFileRead(It.IsAny<Robust.Shared.Utility.ResPath>()))
            .Throws<FileNotFoundException>();
        var type = typeof(IModLoader).Assembly.GetType("Robust.Shared.ContentPack.AssemblyTypeChecker", true)!;
        var checker = Activator.CreateInstance(type, resources.Object, log.GetSawmill("res.typecheck"))!;
        if (engineDirectory != null)
        {
            Func<string, Stream?> launcherLoader = name =>
            {
                var path = Path.Combine(engineDirectory, name);
                return File.Exists(path) ? File.OpenRead(path) : null;
            };
            // ExtraRobustLoader is resolved before the locally compiled DLLs.
            type.GetProperty("ExtraRobustLoader")!.SetValue(checker, launcherLoader);
        }
        type.GetField("EngineModuleDirectories")!.SetValue(checker, new[] { AppContext.BaseDirectory });
        var module = shared ? typeof(Content.Shared.Preferences.HumanoidCharacterProfile).Assembly : typeof(ChatTabsSettings).Assembly;
        using var assembly = File.OpenRead(module.Location);
        var result = (bool) type.GetMethod("CheckAssembly", new[] { typeof(Stream) })!.Invoke(checker, new object[] { assembly })!;
        foreach (var invocation in handler.Invocations)
            TestContext.Out.WriteLine($"{invocation.Arguments[0]}: {invocation.Arguments[1].GetType().GetMethod("RenderMessage", new[] { typeof(IFormatProvider) })!.Invoke(invocation.Arguments[1], new object?[] { null })}");
        Assert.That(result, Is.True, $"{module.GetName().Name} must pass the engine sandbox as well as the compiler.");
    }
}
