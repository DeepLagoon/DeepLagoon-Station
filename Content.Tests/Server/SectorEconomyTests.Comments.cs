using System;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Content.Tests.Server;

public sealed partial class SectorEconomyTests
{
    [Test]
    public async Task MarketplaceCommentsSurviveReopenAreRateLimitedAndCannotMutateMoneyOrClosedOffers()
    {
        var profile = await ProfileId(_user);
        var cycle = await BeginCycle();
        var offer = new TradeCreate(Guid.NewGuid(), _user, profile, true, "Steel", "Steel", "", 1, 100, new(), null, null) { Cycle = cycle.Id };
        await CreateCycleOffer(offer);
        await using (var db = new SqliteServerDbContext(_options))
            await EconomyOperations.AddComment(db, cycle.Id, offer.Operation, _user, "ID Name", "Is it still available?", 20000);
        Assert.That((await BeginCycle()).Id, Is.EqualTo(cycle.Id));
        await using (var db = new SqliteServerDbContext(_options))
        {
            var comments = await EconomyOperations.Comments(db, offer.Operation, 21000);
            Assert.That(comments, Has.Length.EqualTo(1)); Assert.That(comments[0].Author, Is.EqualTo("ID Name"));
            Assert.That(comments[0].Text, Is.EqualTo("Is it still available?"));
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(799900));
            Assert.That(await db.BankFinancialOperations.CountAsync(), Is.EqualTo(1));
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => EconomyOperations.AddComment(db, cycle.Id, offer.Operation, _user, "ID Name", "Spam", 21000))!.Message, Is.EqualTo("comment_rate"));
        }
        await using (var db = new SqliteServerDbContext(_options))
            await EconomyOperations.AddComment(db, cycle.Id, offer.Operation, _user, "Different ID", "Second message", 30000);
        await CloseCycle(cycle.Id);
        await ExpireCycle();
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That(await db.LagoonEconomyRecords.CountAsync(r => r.Id.StartsWith("economy:comment:")), Is.Zero);
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => EconomyOperations.AddComment(db, cycle.Id, offer.Operation, _user, "ID", "Late", 40000))!.Message, Is.EqualTo("round_closed"));
        }
    }
}
