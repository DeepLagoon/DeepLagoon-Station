#nullable enable
using System;
using Content.Client.Entry;
using Moq;
using NUnit.Framework;
using Robust.Client.Console;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Utility;
using Robust.UnitTesting;

namespace Content.Tests.Client;

[TestFixture]
public sealed class DebugConsoleLogFilterTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

#if RELEASE || FULL_RELEASE
    private const bool HideLocalizationDiagnostics = true;
#else
    private const bool HideLocalizationDiagnostics = false;
#endif

    [TestCase(LogLevel.Debug, "root", "HANDLING REQUEST: res://deeplagoon/Web/DeepLagoon/bridge?health", true)]
    [TestCase(LogLevel.Debug, "root", "HANDLING REQUEST: res://deeplagoon/web/deeplagoon/tgui.bundle.js", true)]
    [TestCase(LogLevel.Debug, "web.res", "HANDLING: res://deeplagoon/Web/DeepLagoon/tgui.bundle.js", true)]
    [TestCase(LogLevel.Debug, "root", "HANDLING REQUEST: https://example.invalid/other", false)]
    [TestCase(LogLevel.Debug, "web.res", "HANDLING: res://other/Web/DeepLagoon/foo.css", false)]
    [TestCase(LogLevel.Debug, "root", "HANDLING REQUEST: http://localhost:3000/interface.html", true)]
    [TestCase(LogLevel.Debug, "root", "HANDLING REQUEST: http://localhost:3001/interface.html", false)]
    [TestCase(LogLevel.Error, "tgui", "Browser initialization failed", true)]
    [TestCase(LogLevel.Warning, "tgui.page", "Script warning", true)]
    [TestCase(LogLevel.Warning, "loc", "Unknown messageId", HideLocalizationDiagnostics)]
    [TestCase(LogLevel.Error, "loc", "Fluent formatting error", HideLocalizationDiagnostics)]
    [TestCase(LogLevel.Debug, "loc", "Flushing entity localization cache", false)]
    [TestCase(LogLevel.Error, "net", "Connection failed", false)]
    public void FiltersStockConsoleLogOutput(LogLevel level, string category, string body, bool hidden)
    {
        FormattedMessage? output = null;
        var console = new Mock<IClientConsoleHost>();
        console.Setup(host => host.AddFormattedLine(It.IsAny<FormattedMessage>()))
            .Callback((FormattedMessage message) => output = message);

        using var log = new LogManager();
        log.RootSawmill.AddHandler(CreateStockHandler(console.Object));
        log.GetSawmill(category).Log(level, body);

        Assert.That(output, Is.Not.Null);
        Assert.That(DebugConsoleLogFilter.IsHiddenLog(output!, "http://localhost:3000"), Is.EqualTo(hidden));
    }

    [Test]
    public void LeavesPlainConsoleOutputAlone()
    {
        var output = new FormattedMessage();
        output.AddText("[ERRO] loc: Example console command output.");

        Assert.That(DebugConsoleLogFilter.IsHiddenLog(output), Is.False);
    }

    [Test]
    public void RemovesHiddenMessagesAfterTheConsoleDrainsItsQueue()
    {
        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        ui.InitializeTesting();
        var output = new OutputPanel();
        ui.RootControl.AddChild(output);
        try
        {
            FormattedMessage? queued = null;
            var console = new Mock<IClientConsoleHost>();
            console.Setup(host => host.AddFormattedLine(It.IsAny<FormattedMessage>()))
                .Callback((FormattedMessage message) =>
                {
                    queued = message;
                    console.Raise(host => host.AddFormatted += null, new AddFormattedMessageArgs(message));
                });

            using var filter = new DebugConsoleLogFilter(console.Object, output, Mock.Of<IConfigurationManager>());
            using var log = new LogManager();
            log.RootSawmill.AddHandler(CreateStockHandler(console.Object));
            log.GetSawmill("root").Debug("HANDLING REQUEST: res://deeplagoon/Web/DeepLagoon/bridge?health");

            filter.FilterOutput();
            output.AddText("Console command result");
            output.AddMessage(queued!);
            filter.FilterOutput();

            Assert.That(output.EntryCount, Is.EqualTo(1));
            Assert.That(output.GetMessage(0).ToMarkup(), Is.EqualTo("Console command result"));
        }
        finally
        {
            output.Orphan();
        }
    }

    private static ILogHandler CreateStockHandler(IClientConsoleHost console)
    {
        var handlerType = typeof(IClientConsoleHost).Assembly.GetType("Robust.Client.Log.DebugConsoleLogHandler", true)!;
        return (ILogHandler) Activator.CreateInstance(handlerType, console)!;
    }
}
