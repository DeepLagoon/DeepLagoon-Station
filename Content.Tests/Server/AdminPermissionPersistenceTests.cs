using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;

namespace Content.Tests.Server;

[TestFixture]
public sealed class AdminPermissionPersistenceTests
{
    [Test]
    public async Task OneFlagEditRetainsUnchangedDatabaseRowsForAdminsAndRanks()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        var path = Path.Combine(Path.GetTempPath(), $"lagoon-admin-flags-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            var backend = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
            var user = Guid.NewGuid();
            var rank = new AdminRank { Name = "Test", ShortName = "T", Flags = new() { new() { Flag = "BAN" } } };
            await backend.AddAdminRankAsync(rank, CancellationToken.None);
            await backend.AddAdminAsync(new Admin
            {
                UserId = user, AdminRankId = rank.Id,
                Flags = new() { new() { Flag = "KICK", Negative = false }, new() { Flag = "DEBUG", Negative = true } },
            }, CancellationToken.None);

            int rankFlagId, adminFlagId, negativeFlagId;
            await using (var db = new SqliteServerDbContext(options))
            {
                rankFlagId = (await db.Set<AdminRankFlag>().SingleAsync()).Id;
                adminFlagId = (await db.Set<AdminFlag>().SingleAsync(f => f.Flag == "KICK")).Id;
                negativeFlagId = (await db.Set<AdminFlag>().SingleAsync(f => f.Flag == "DEBUG")).Id;
            }

            rank.Flags = new() { new() { Flag = "NAMECOLOR" }, new() { Flag = "BAN" } };
            await backend.UpdateAdminRankAsync(rank, CancellationToken.None);
            await backend.UpdateAdminAsync(new Admin
            {
                UserId = user, AdminRankId = rank.Id,
                Flags = new() { new() { Flag = "NAMECOLOR" }, new() { Flag = "DEBUG", Negative = true }, new() { Flag = "KICK" } },
            }, CancellationToken.None);

            await using (var db = new SqliteServerDbContext(options))
            {
                Assert.That((await db.Set<AdminRankFlag>().SingleAsync(f => f.Flag == "BAN")).Id, Is.EqualTo(rankFlagId));
                Assert.That((await db.Set<AdminFlag>().SingleAsync(f => f.Flag == "KICK")).Id, Is.EqualTo(adminFlagId));
                Assert.That((await db.Set<AdminFlag>().SingleAsync(f => f.Flag == "DEBUG")).Id, Is.EqualTo(negativeFlagId));
                Assert.That(await db.Set<AdminFlag>().CountAsync(), Is.EqualTo(3));
                Assert.That(await db.Set<AdminRankFlag>().CountAsync(), Is.EqualTo(2));
            }

            // A same-set save with fresh entity objects must also retain row IDs.
            rank.Flags = new() { new() { Flag = "BAN" }, new() { Flag = "NAMECOLOR" } };
            await backend.UpdateAdminRankAsync(rank, CancellationToken.None);
            await using var final = new SqliteServerDbContext(options);
            Assert.That((await final.Set<AdminRankFlag>().SingleAsync(f => f.Flag == "BAN")).Id, Is.EqualTo(rankFlagId));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
