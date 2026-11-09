#nullable enable
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Content.Client.PDA;
using Content.Server.Examine;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.PdaMessenger;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mind;
using Content.Shared.PDA;
using Content.Shared.Preferences;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using ClientMessenger = Content.Client._DeepLagoon.PdaMessenger.PdaMessengerSystem;
using ClientUi = Robust.Client.GameObjects.UserInterfaceSystem;
using ServerMessenger = Content.Server._DeepLagoon.PdaMessenger.PdaMessengerSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class PdaMessengerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Regex PdaNumber = new(@"\b[1-8]\d{8}\b");

    [Test]
    public async Task OnlySavedNumbersCanSendAndContactsSurviveRoundCleanup()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var player = pair.Player!;
        var recipient = await server.AddDummySession();
        var entities = server.EntMan;
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var serverMessenger = entities.System<ServerMessenger>();
        var register = typeof(ServerMessenger).GetMethod("OnSpawn", Private)!;
        var request = typeof(ServerMessenger).GetMethod("OnRequest", Private)!;
        var examine = entities.System<ExamineSystem>();
        var snapshotField = typeof(ClientMessenger).GetField("_state", Private)!;
        await PoolManager.WaitUntil(server, () => preferences.TryGetCachedPreferences(recipient.UserId, out _)
            && preferences.TryGetCachedPreferences(player.UserId, out _), 600);
        await pair.Client.WaitPost(() => pair.Client.ResolveDependency<IConfigurationManager>()
            .SetCVar(WebUiCVars.InterfaceStyle, 2));

        (EntityUid Body, EntityUid Pda) Spawn(ICommonSession session)
        {
            var body = entities.SpawnEntity("MobHuman", map.GridCoords);
            var pda = entities.SpawnEntity("PassengerPDA", map.GridCoords);
            var minds = entities.System<SharedMindSystem>();
            minds.TransferTo(minds.CreateMind(session.UserId), body);
            Assert.That(entities.System<SharedHandsSystem>().TryPickup(body, pda), Is.True);
            entities.System<SharedUserInterfaceSystem>().OpenUi(pda, PdaUiKey.Key, body);
            return (body, pda);
        }

        void Register(ICommonSession session, EntityUid body)
        {
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).SelectedCharacter;
            register.Invoke(serverMessenger, [new PlayerSpawnCompleteEvent(body, session, null, false, false, 1, default, profile)]);
        }

        (EntityUid Body, EntityUid Pda) sender = default, listener = default;
        await server.WaitAssertion(() =>
        {
            sender = Spawn(player);
            listener = Spawn(recipient);
            Register(player, sender.Body);
            Register(recipient, listener.Body);
        });

        int Number(EntityUid pda, EntityUid observer)
        {
            var text = examine.GetExamineText(pda, observer).ToString();
            var match = PdaNumber.Match(text);
            return match.Success ? int.Parse(match.Value) : 0;
        }

        var ownNumber = 0;
        var peerNumber = 0;
        await PoolManager.WaitUntil(server, async () =>
        {
            await server.WaitPost(() =>
            {
                ownNumber = Number(sender.Pda, listener.Body);
                peerNumber = Number(listener.Pda, sender.Body);
            });
            return ownNumber != 0 && peerNumber != 0;
        });
        Assert.That(peerNumber, Is.Not.EqualTo(ownNumber));

        PdaMenu menu = null!;
        var device = entities.GetNetEntity(sender.Pda);
        PdaMessengerSnapshot? Current() => (PdaMessengerSnapshot?) snapshotField.GetValue(pair.Client.EntMan.System<ClientMessenger>());
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            Assert.That(client.System<ClientUi>().TryGetOpenUi(client.GetEntity(device), PdaUiKey.Key, out var bui), Is.True);
            menu = (PdaMenu) typeof(PdaBoundUserInterface).GetField("_menu", Private)!.GetValue(bui)!;
            menu.ChangeView(PdaMenu.MessengerScreen);
        });
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Current()!.Number, Is.EqualTo(ownNumber));
            Assert.That(Current()!.Contacts, Is.Empty);
            Assert.That(menu.MessengerView.Visible, Is.True);
        });

        void ServerRequest(ICommonSession user, EntityUid pda, PdaMessengerAction action, int target = 0, string text = "") =>
            request.Invoke(serverMessenger, [new PdaMessengerRequest(entities.GetNetEntity(pda), action, target, text), new EntitySessionEventArgs(user)]);

        await server.WaitAssertion(() =>
        {
            ServerRequest(player, sender.Pda, PdaMessengerAction.AddContact, text: ownNumber.ToString());
            ServerRequest(recipient, listener.Pda, PdaMessengerAction.Open);
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() => Assert.That(Current()!.Contacts, Is.Empty));
        await Task.Delay(550);
        await server.WaitAssertion(() =>
        {
            ServerRequest(player, sender.Pda, PdaMessengerAction.AddContact, text: peerNumber.ToString());
            ServerRequest(player, sender.Pda, PdaMessengerAction.Send, peerNumber, "Station test");
            ServerRequest(recipient, listener.Pda, PdaMessengerAction.Select, ownNumber);
            ServerRequest(recipient, listener.Pda, PdaMessengerAction.Send, ownNumber, "Unsaved reply");
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Current()!.Contacts.Single(), Is.EqualTo(new PdaMessengerContact(peerNumber, true, true, false)));
            Assert.That(Current()!.Messages.Select(message => message.Text), Is.EqualTo(new[] { "Station test" }));
        });

        await Task.Delay(1050);
        await server.WaitAssertion(() =>
        {
            ServerRequest(recipient, listener.Pda, PdaMessengerAction.AddContact, text: ownNumber.ToString());
            ServerRequest(recipient, listener.Pda, PdaMessengerAction.Send, ownNumber, "Saved reply");
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Current()!.Messages.Select(message => message.Text), Is.EqualTo(new[] { "Station test", "Saved reply" }));
            menu.ToHomeScreen();
        });
        await pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            typeof(ServerMessenger).GetMethod("ResetRound", Private)!.Invoke(serverMessenger, null);
            Register(player, sender.Body);
        });
        await PoolManager.WaitUntil(server, async () =>
        {
            var number = 0;
            await server.WaitPost(() => number = Number(sender.Pda, listener.Body));
            return number == ownNumber;
        });
        await pair.Client.WaitAssertion(() => menu.ChangeView(PdaMenu.MessengerScreen));
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Current()!.Number, Is.EqualTo(ownNumber));
            Assert.That(Current()!.Contacts.Single().Id, Is.EqualTo(peerNumber));
            Assert.That(Current()!.Messages, Is.Empty);
        });
        await pair.CleanReturnAsync();
    }
}
