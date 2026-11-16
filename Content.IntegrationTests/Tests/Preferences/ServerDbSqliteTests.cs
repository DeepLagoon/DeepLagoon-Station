using System.Collections.Generic;
using System.Linq;
using Content.Server.Database;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Preferences.Loadouts.Effects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.Preferences
{
    [TestFixture]
    public sealed class ServerDbSqliteTests
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: dataset
  id: sqlite_test_names_first_male
  values:
  - Aaden

- type: dataset
  id: sqlite_test_names_first_female
  values:
  - Aaliyah

- type: dataset
  id: sqlite_test_names_last
  values:
  - Ackerley";

        private static HumanoidCharacterProfile CharlieCharlieson()
        {
            return new HumanoidCharacterProfile() // Frontier - added HumanoidCharacterProfile
            {
                Name = "Charlie Charlieson",
                FlavorText = "**The biggest boy around.** -=#ff8800(цвет)=-",
                OocNotes = "__Заметка от игрока__",
                HeadshotId = "0123456789abcdef0123456789abcdef",
                Species = "Human",
                Age = 21,
                Appearance = new(
                    "Afro",
                    Color.Aqua,
                    "Shaved",
                    Color.Aquamarine,
                    Color.Azure,
                    Color.Beige,
                    new ())
            }.WithBankBalance(27000); // Frontier - accessor issue
        }

        private static ServerDbSqlite GetDb(RobustIntegrationTest.ServerIntegrationInstance server)
        {
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var opsLog = server.ResolveDependency<ILogManager>().GetSawmill("db.ops");
            var builder = new DbContextOptionsBuilder<SqliteServerDbContext>();
            var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            builder.UseSqlite(conn);
            return new ServerDbSqlite(() => builder.Options, true, cfg, true, opsLog);
        }

        [Test]
        public async Task TestUserDoesNotExist()
        {
            var pair = await PoolManager.GetServerClient();
            var db = GetDb(pair.Server);
            // Database should be empty so a new GUID should do it.
            Assert.That(await db.GetPlayerPreferencesAsync(NewUserId()), Is.Null);

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TestInitPrefs()
        {
            var pair = await PoolManager.GetServerClient();
            var db = GetDb(pair.Server);
            var username = new NetUserId(new Guid("640bd619-fc8d-4fe2-bf3c-4a5fb17d6ddd"));
            const int slot = 0;
            var originalProfile = CharlieCharlieson();
            await db.InitPrefsAsync(username, originalProfile);
            var prefs = await db.GetPlayerPreferencesAsync(username);
            Assert.That(prefs.Characters.Single(p => p.Key == slot).Value.MemberwiseEquals(originalProfile));
            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task DeselectedItemCustomizationSurvivesDatabaseReload()
        {
            var pair = await PoolManager.GetServerClient();
            var db = GetDb(pair.Server);
            var user = NewUserId();
            var role = new RoleLoadout("DLEinsteinPersonal");
            role.Customizations["DLEELoadoutClothingJumpsuitSuitBlack"] = new Content.Shared._DeepLagoon.Loadouts.PersonalLoadoutCustomization
                { Name = "Моя униформа", Description = "Русское описание", Color = "#123456FF" };
            var profile = CharlieCharlieson().WithLoadout(role);
            await db.InitPrefsAsync(user, profile);
            var loaded = (HumanoidCharacterProfile)(await db.GetPlayerPreferencesAsync(user))!.Characters[0];
            Assert.That(loaded.Loadouts[role.Role.Id].SelectedLoadouts, Is.Empty);
            Assert.That(loaded.Loadouts[role.Role.Id].Customizations, Is.EqualTo(role.Customizations));
            await db.SaveCharacterSlotAsync(user, loaded.WithName("Another Name"), 0);
            var reloaded = (HumanoidCharacterProfile)(await db.GetPlayerPreferencesAsync(user))!.Characters[0];
            Assert.That(reloaded.Loadouts[role.Role.Id].Customizations, Is.EqualTo(role.Customizations));
            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task BackpackLayoutSurvivesDatabaseReload()
        {
            var pair = await PoolManager.GetServerClient();
            var db = GetDb(pair.Server);
            var user = NewUserId();
            var profile = CharlieCharlieson().WithLoadout(new RoleLoadout("JobContractor"));
            await db.InitPrefsAsync(user, profile);
            var loaded = (HumanoidCharacterProfile)(await db.GetPlayerPreferencesAsync(user))!.Characters[0];
            Assert.That(loaded.Loadouts["JobContractor"].BackpackLayout, Is.Empty);

            var role = loaded.Loadouts["JobContractor"].Clone();
            role.BackpackLayout["loadout/ContractorAppraisalCartridge/0/AppraisalCartridge"] = new(false, 3, 2, 90);
            role.BackpackLayout["loadout/ContractorClothingMaskGas/0/ClothingMaskGas"] = new(true, 0, 0, 0);
            await db.SaveCharacterSlotAsync(user, loaded.WithLoadout(role), 0);

            var reloaded = (HumanoidCharacterProfile)(await db.GetPlayerPreferencesAsync(user))!.Characters[0];
            Assert.That(reloaded.Loadouts["JobContractor"].BackpackLayout, Is.EqualTo(role.BackpackLayout));
            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TestDeleteCharacter()
        {
            var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var db = GetDb(server);
            var username = new NetUserId(new Guid("640bd619-fc8d-4fe2-bf3c-4a5fb17d6ddd"));
            await db.InitPrefsAsync(username, new HumanoidCharacterProfile());
            await db.SaveCharacterSlotAsync(username, CharlieCharlieson(), 1);
            await db.SaveSelectedCharacterIndexAsync(username, 1);
            await db.SaveCharacterSlotAsync(username, null, 1);
            var prefs = await db.GetPlayerPreferencesAsync(username);
            Assert.That(!prefs.Characters.Any(p => p.Key != 0));
            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TestNoPendingDatabaseChanges()
        {
            var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var db = GetDb(server);
            Assert.That(async () => await db.HasPendingModelChanges(), Is.False,
                "The database has pending model changes. Add a new migration to apply them. See https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations");
            await pair.CleanReturnAsync();
        }

        private static NetUserId NewUserId()
        {
            return new(Guid.NewGuid());
        }
    }
}
