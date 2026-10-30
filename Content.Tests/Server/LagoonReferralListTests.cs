using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class LagoonReferralListTests
{
    [Test]
    public async Task ListOnlyContainsOwnInviteesAndTheirEarnedMilestones()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        await using var db = new SqliteServerDbContext(new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        void Add(string name, Guid referrer, int stage, bool rewarded, bool falseMatch = false)
        {
            var id = Guid.NewGuid();
            db.Player.Add(new Player { UserId = id, LastSeenUserName = name, LastSeenAddress = IPAddress.Loopback });
            var state = new LagoonEconomyState { Referrer = referrer, ReferralStage = stage, ReferrerReward = rewarded,
                ApartmentLabel = falseMatch ? owner.ToString() : "" };
            db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = "account:" + id.ToString("N"), UserId = id, Data = JsonSerializer.Serialize(state) });
        }
        Add("New", owner, 0, false);
        Add("First", owner, 1, true);
        Add("Complete", owner, 2, true);
        Add("Capped", owner, 2, false);
        Add("Other", other, 2, true, true);
        await db.SaveChangesAsync();
        var result = await AccountEconomyOperations.Referrals(db, owner);
        Assert.That(result.Select(row => row.Name), Is.EquivalentTo(new[] { "New", "First", "Complete", "Capped" }));
        Assert.That(result.Single(row => row.Name == "New").Reward, Is.Zero);
        Assert.That(result.Single(row => row.Name == "First").Reward, Is.EqualTo(25));
        Assert.That(result.Single(row => row.Name == "Complete").Reward, Is.EqualTo(100));
        Assert.That(result.Single(row => row.Name == "Capped").Reward, Is.Zero);
        Assert.That(await AccountEconomyOperations.Referrals(db, Guid.NewGuid()), Is.Empty);
    }
}
