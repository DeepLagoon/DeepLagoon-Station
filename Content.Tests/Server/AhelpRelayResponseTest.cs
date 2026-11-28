using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Ahelp;
using Moq;
using NUnit.Framework;
using Robust.Server.ServerStatus;

namespace Content.Tests.Server;

[TestFixture]
public sealed class AhelpRelayResponseTest
{
    private static Task Respond(IStatusHandlerContext context, object payload, HttpStatusCode code) =>
        (Task) typeof(AhelpSystem).GetMethod("RespondRelayAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, payload, code])!;

    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(false, 3)]
    [TestCase(true, 0)]
    public async Task ClosedConsumerDoesNotCauseAnotherResponse(bool errorResponse, int failure)
    {
        Exception exception = failure switch
        {
            0 => new Win32Exception(32, "Broken pipe"),
            1 => new IOException("Connection reset by peer"),
            2 => new OperationCanceledException(),
            _ => new Win32Exception(unchecked((int) 0x80131620), "Unable to write data to the transport connection"),
        };
        var context = new Mock<IStatusHandlerContext>(MockBehavior.Strict);
        if (errorResponse)
            context.Setup(c => c.RespondErrorAsync(HttpStatusCode.Unauthorized)).ThrowsAsync(exception);
        else
            context.Setup(c => c.RespondJsonAsync(It.IsAny<object>(), HttpStatusCode.OK)).ThrowsAsync(exception);

        await Respond(context.Object, errorResponse ? null : new { events = Array.Empty<object>() },
            errorResponse ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        if (errorResponse)
            context.Verify(c => c.RespondErrorAsync(HttpStatusCode.Unauthorized), Times.Once);
        else
            context.Verify(c => c.RespondJsonAsync(It.IsAny<object>(), HttpStatusCode.OK), Times.Once);
        context.VerifyNoOtherCalls();
    }

    [Test]
    public void UnexpectedResponseFailuresAreNotHidden()
    {
        var context = new Mock<IStatusHandlerContext>(MockBehavior.Strict);
        context.Setup(c => c.RespondJsonAsync(It.IsAny<object>(), HttpStatusCode.OK))
            .ThrowsAsync(new InvalidOperationException("unexpected serialization failure"));
        Assert.ThrowsAsync<InvalidOperationException>(() => Respond(context.Object, new { ok = true }, HttpStatusCode.OK));
        context.Verify(c => c.RespondJsonAsync(It.IsAny<object>(), HttpStatusCode.OK), Times.Once);
        context.VerifyNoOtherCalls();
    }
}
