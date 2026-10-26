using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using Content.Client.Lobby.UI.Loadouts;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Graphics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Localization;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DiscordBoostLoadoutTests
{
    [TestPrototypes]
    private const string Prototypes = """
        - type: loadout
          id: TestDiscordBoosterLoadout
          personalRequirements:
          - kind: DiscordBoostRequirement

        - type: loadout
          id: TestBoostyTier1
          personalDonor: Boosty
          personalDonorTier: 1
          personalItems: [ClothingUniformJumpsuitColorGrey]
        - type: loadout
          id: TestBoostyTier2
          personalDonor: Boosty
          personalDonorTier: 2
          personalItems: [ClothingUniformJumpsuitColorGrey]
        - type: loadout
          id: TestBoostyTier3
          personalDonor: Boosty
          personalDonorTier: 3
          personalItems: [ClothingUniformJumpsuitColorGrey]
        """;

    [Test]
    public async Task ServerRejectsSavedBoosterEquipmentAfterRevocation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var systems = server.ResolveDependency<IEntitySystemManager>();
        var linking = systems.GetEntitySystem<DiscordLinkSystem>();
        var loadouts = systems.GetEntitySystem<PersonalLoadoutSystem>();
        var config = server.ResolveDependency<IConfigurationManager>();
        var directory = Path.Combine(Path.GetTempPath(), "ss14-boost-test-" + Guid.NewGuid());
        using var store = new DiscordLinkStore(Path.Combine(directory, "links.db"));
        var storeField = typeof(DiscordLinkSystem).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var enabledField = typeof(DiscordLinkSystem).GetField("_enabled", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await server.WaitPost(() =>
        {
            var admins = server.ResolveDependency<IAdminManager>();
            foreach (var player in server.ResolveDependency<Robust.Server.Player.IPlayerManager>().Sessions)
            {
                if (admins.GetAdminData(player, true) is {} admin) admin.Flags &= ~AdminFlags.Host;
                admins.GetType().GetMethod("UpdateAdminStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(admins, [player]);
            }
            config.SetCVar(CCVars.AutoVoteEnabled, false);
            config.SetCVar(CCVars.MapAutoVoteEnabled, false);
            config.SetCVar(CCVars.PresetAutoVoteEnabled, false);
            config.SetCVar(CCVars.VoteEnabled, false);
            var votes = server.ResolveDependency<Content.Server.Voting.Managers.IVoteManager>();
            foreach (var vote in votes.ActiveVotes.ToArray())
                vote.Cancel();
            votes.Update();
            config.SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            storeField.SetValue(linking, store);
            enabledField.SetValue(linking, true);
        });
        var session = await server.AddDummySession();
        await PoolManager.WaitUntil(server, () => session.Status == SessionStatus.InGame, 600);
        try
        {
            await server.WaitAssertion(() =>
            {
                // Ordinary supporter tests must not inherit the local integration HOST override.
                if (server.ResolveDependency<IAdminManager>().GetAdminData(session, true) is {} admin)
                    admin.Flags &= ~AdminFlags.Host;
                // Exercise the authenticated production path with the test transport.
                var auth = session.GetType().GetProperty("AuthType")!;
                auth.SetValue(session, LoginType.LoggedIn);
                const string discord = "1554565156657299597";
                store.Consume(discord, store.Issue(session.UserId.UserId, session.Name));
                var prototype = server.ResolveDependency<IPrototypeManager>().Index<LoadoutPrototype>("TestDiscordBoosterLoadout");
                var profile = HumanoidCharacterProfile.DefaultWithSpecies();
                Assert.That(systems.GetEntitySystem<SharedDiscordBoostSystem>(), Is.SameAs(linking));
                Assert.That(loadouts.CanUse(prototype, profile, "Passenger", session, out _), Is.False);
                store.SetBoost(discord, true);
                Assert.That(loadouts.GetPoints(session), Is.EqualTo(loadouts.Points + 6));
                Assert.That(loadouts.CanUse(prototype, profile, "Passenger", session, out _), Is.True);
                Assert.That(loadouts.CanUse(prototype, profile, "Passenger", null, out _), Is.False);
                auth.SetValue(session, LoginType.GuestAssigned);
                Assert.That(loadouts.CanUse(prototype, profile, "Passenger", session, out _), Is.False);
                auth.SetValue(session, LoginType.LoggedIn);
                store.SetBoost(discord, false);
                Assert.That(loadouts.CanUse(prototype, profile, "Passenger", session, out var reason), Is.False);
                Assert.That(reason, Does.Contain("Discord"));
                var supporters = systems.GetEntitySystem<SharedDiscordBoostSystem>();
                var prototypes = server.ResolveDependency<IPrototypeManager>();
                var colors = new[] { 0x112233, 0x223344, 0x334455, 0x445566 };
                foreach (var tier in new[] { 3, 2, 1, 0 })
                {
                    store.SetSupporter(discord, false, tier, colors);
                    Assert.That(loadouts.GetPoints(session), Is.EqualTo(loadouts.Points + (tier > 0 ? 3 * (tier + 1) : 0)));
                    var headshots = systems.GetEntitySystem<Content.Server._DeepLagoon.CharacterInfo.HeadshotSystem>();
                    var extended = (bool)headshots.GetType().GetMethod("Extended", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(headshots, new object[] { session })!;
                    Assert.That(extended, Is.EqualTo(tier >= 2), "GIF and 5 MB are available only from Boosty level 2");
                    Assert.That(supporters.HasActiveBoost(session), Is.False, "Boosty must not fabricate a Discord boost");
                    Assert.That(loadouts.CanUse(prototype, profile, "Passenger", session, out _), Is.EqualTo(tier > 0));
                    for (var required = 1; required <= 3; required++)
                    {
                        var id = new Robust.Shared.Prototypes.ProtoId<LoadoutPrototype>("TestBoostyTier" + required);
                        var item = prototypes.Index(id);
                        Assert.That(loadouts.CanUse(item, profile, "Passenger", session, out _), Is.EqualTo(tier >= required));
                    }
                }
                store.SetSupporter(discord, true, 0, colors);
                Assert.That(supporters.GetBoostyTier(session), Is.Zero);
                Assert.That(supporters.GetProductTier(session), Is.EqualTo(2));
                Assert.That(supporters.GetSupporterTier(session), Is.EqualTo(1));
                Assert.That(supporters.HasOocColorAccess(session), Is.True);
                Assert.That(loadouts.CanUse(prototypes.Index(new Robust.Shared.Prototypes.ProtoId<LoadoutPrototype>("TestBoostyTier1")), profile, "Passenger", session, out _), Is.True);
                Assert.That(loadouts.CanUse(prototypes.Index(new Robust.Shared.Prototypes.ProtoId<LoadoutPrototype>("TestBoostyTier2")), profile, "Passenger", session, out _), Is.False);
                store.SetSupporter(discord, false, 0, colors);
                Assert.That(supporters.GetProductTier(session), Is.Zero);
                Assert.That(supporters.GetDonorColor(DonorCategory.Boosty, 1).ToHex(), Does.StartWith("#223344"));
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                enabledField.SetValue(linking, false);
                storeField.SetValue(linking, null);
            });
            await server.RemoveDummySession(session);
        }
        var boosterNetwork = server.ResolveDependency<IEntityManager>().EntityNetManager!;
        await server.WaitPost(() =>
        {
            var realSession = server.ResolveDependency<Robust.Server.Player.IPlayerManager>().Sessions.Single();
            boosterNetwork.SendSystemNetworkMessage(new DiscordBoostStatusEvent(900), realSession.Channel);
        });
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            var local = pair.Client.ResolveDependency<Robust.Client.Player.IPlayerManager>().LocalSession;
            var supporters = pair.Client.ResolveDependency<IEntitySystemManager>().GetEntitySystem<SharedDiscordBoostSystem>();
            Assert.That(supporters.GetProductTier(local), Is.EqualTo(2));
            Assert.That(supporters.GetSupporterTier(local), Is.EqualTo(1));
            Assert.That(supporters.GetLoadoutPointBonus(local), Is.EqualTo(6));
            Assert.That(supporters.HasOocColorAccess(local), Is.True);
        });
        // Exercise delivery of tier and palette data over the real test connection.
        var network = server.ResolveDependency<IEntityManager>().EntityNetManager!;
        await server.WaitPost(() =>
        {
            var realSession = server.ResolveDependency<Robust.Server.Player.IPlayerManager>().Sessions.Single();
            network.SendSystemNetworkMessage(new DiscordBoostStatusEvent(0, 2, 900, new[] { 0x112233, 0x223344, 0x334455, 0x445566 }), realSession.Channel);
        });
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            var local = pair.Client.ResolveDependency<Robust.Client.Player.IPlayerManager>().LocalSession;
            var clientSystems = pair.Client.ResolveDependency<IEntitySystemManager>();
            var supporters = clientSystems.GetEntitySystem<SharedDiscordBoostSystem>();
            Assert.That(supporters.GetBoostyTier(local), Is.EqualTo(2));
            Assert.That(supporters.HasActiveBoost(local), Is.False);
            var editor = new PersonalLoadoutEditor();
            var editorType = typeof(PersonalLoadoutEditor);
            editorType.GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, HumanoidCharacterProfile.DefaultWithSpecies());
            editorType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, local);
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var items = Enumerable.Range(1, 3).Select(tier => prototypes.Index(new ProtoId<LoadoutPrototype>("TestBoostyTier" + tier))).ToList();
            var list = (Control) editorType.GetMethod("MakeList", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor,
                new object?[] { items, new RoleLoadout(PersonalLoadoutSystem.Role), new Dictionary<string, Loadout>(), 0, null })!;
            list.Measure(new Vector2(640, 480));
            list.Arrange(new Robust.Shared.Maths.UIBox2(0, 0, 640, 480));
            var buttons = Descendants(list).OfType<Button>().Where(b => b.ToolTip?.Contains("Boosty") == true).ToArray();
            Assert.That(buttons.Length, Is.EqualTo(3));
            for (var tier = 1; tier <= 3; tier++)
            {
                var donorLabel = Loc.GetString("dl-loadout-donor-boosty", ("tier", tier));
                var button = buttons.Single(b => b.ToolTip!.Split('\n').Contains(donorLabel));
                Assert.That(button.Disabled, Is.EqualTo(tier == 3));
                Assert.That(Descendants(button).OfType<TextureRect>().Count(), Is.EqualTo(tier == 3 ? 2 : 1), "Locked tiles keep the item image and add a lock");
                var style = (StyleBoxFlat) button.StyleBoxOverride!;
                Assert.That(style.BorderColor, Is.EqualTo(supporters.GetDonorColor(DonorCategory.Boosty, tier)));
                Assert.That(button.Size.X, Is.EqualTo(156).Within(2), "Tier labels must fit within the tile");
            }
            list.Dispose();
            editor.Dispose();
        });
        await pair.CleanReturnAsync();
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

}
