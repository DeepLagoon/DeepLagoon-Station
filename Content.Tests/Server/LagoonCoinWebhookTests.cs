using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Discord;
using Moq;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class LagoonCoinWebhookTests
{
    [TestCase("admin:1", 5, "Начисление Lagoon Coin")]
    [TestCase("purchase:order-1", 100, "Покупка Lagoon Coin")]
    [TestCase("discord:1", -5, "Снятие Lagoon Coin")]
    [TestCase("spend:order-1", -5, "Трата Lagoon Coin")]
    [TestCase("transfer:1", -5, "Перевод Lagoon Coin")]
    [TestCase("transfer:1", 5, "Перевод Lagoon Coin")]
    public void FormatsOperationWithExactBalanceAndNoMentions(string key, long amount, string title)
    {
        var row = new LagoonCoinOperation { UserId = Guid.NewGuid(), OperationId = key,
            Amount = amount, BalanceAfter = 105, Reason = "Причина @everyone", CreatedAt = DateTime.UtcNow };
        var payload = LagoonCoinWebhookFormatter.Format(row, "DeepLagoon", "Игрок", "Автор");
        var embed = payload.Embeds!.Single();
        Assert.That(embed.Title, Is.EqualTo(title));
        Assert.That(embed.Description, Is.EqualTo(row.Reason));
        Assert.That(embed.Fields.Single(f => f.Name == "Баланс после операции").Value, Is.EqualTo("105 LC"));
        Assert.That(embed.Fields.Single(f => f.Name == "Операция").Value, Is.EqualTo(key));
        Assert.That(embed.Fields.Single(f => f.Name == "Игрок").Value, Is.EqualTo("Игрок"));
        Assert.That(embed.Fields.Single(f => f.Name == "Автор").Value, Is.EqualTo("Автор"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.That(json.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength(), Is.Zero);
    }

    [TestCase(HttpStatusCode.OK, true)]
    [TestCase(HttpStatusCode.TooManyRequests, false)]
    [TestCase(HttpStatusCode.InternalServerError, false)]
    public async Task MarksDeliveredOnlyAfterSuccessfulHttpAcknowledgement(HttpStatusCode status, bool delivered)
    {
        var handler = new Handler(status);
        using var http = new HttpClient(handler);
        var db = new Mock<IServerDbManager>();
        db.Setup(d => d.CompleteLagoonCoinWebhookAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        db.Setup(d => d.RetryLagoonCoinWebhookAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var row = new LagoonCoinOperation { UserId = Guid.NewGuid(), OperationId = "purchase:order-1", Amount = 5, BalanceAfter = 5 };
        var owner = Guid.NewGuid();
        var result = await LagoonCoinWebhookSystem.Send(http, db.Object, "https://discord.com/api/webhooks/1/test",
            row, owner, LagoonCoinWebhookFormatter.Format(row, "Test", "Player", "System"));
        Assert.That(result.Delivered, Is.EqualTo(delivered));
        Assert.That(handler.Url, Does.EndWith("?wait=true"));
        if (status == HttpStatusCode.TooManyRequests) Assert.That(result.RetryAfter.TotalSeconds, Is.EqualTo(12));
        db.Verify(d => d.CompleteLagoonCoinWebhookAsync(row.UserId, row.OperationId, owner, It.IsAny<CancellationToken>()),
            delivered ? Times.Once() : Times.Never());
        db.Verify(d => d.RetryLagoonCoinWebhookAsync(row.UserId, row.OperationId, owner, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            delivered ? Times.Never() : Times.Once());
    }

    [Test]
    public void DiscordAuditIdsAreNotShownInWebhook()
    {
        var row = new LagoonCoinOperation { UserId = Guid.NewGuid(), ActorId = Guid.NewGuid(), OperationId = "discord:123",
            Amount = 1, Reason = "Discord actor=1554164947804487770; Награда" };
        var payload = LagoonCoinWebhookFormatter.Format(row, "Test", "Player", "Admin");
        var json = JsonSerializer.Serialize(payload);
        Assert.That(payload.Embeds!.Single().Description, Is.EqualTo("Награда"));
        Assert.That(json, Does.Not.Contain(row.UserId.ToString()));
        Assert.That(json, Does.Not.Contain(row.ActorId.ToString()));
        Assert.That(json, Does.Not.Contain("1554164947804487770"));
    }

    [Test]
    public void TransportFailureRetainsPendingReceipt()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, fail: true));
        var db = new Mock<IServerDbManager>();
        var row = new LagoonCoinOperation { UserId = Guid.NewGuid(), OperationId = "admin:1", Amount = 5 };
        Assert.ThrowsAsync<HttpRequestException>(async () => await LagoonCoinWebhookSystem.Send(http, db.Object,
            "https://discord.com/api/webhooks/1/test", row, Guid.NewGuid(), new WebhookPayload()));
        db.Verify(d => d.CompleteLagoonCoinWebhookAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private sealed class Handler(HttpStatusCode status, bool fail = false) : HttpMessageHandler
    {
        public string Url = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            if (fail) throw new HttpRequestException("Simulated failure");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"retry_after\":12}") });
        }
    }
}
