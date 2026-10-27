using Content.Client.Lobby;
using Content.Server.Preferences.Managers;
using Content.Shared.Preferences;
using Robust.Client.State;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests.Lobby
{
    [TestFixture]
    [TestOf(typeof(ClientPreferencesManager))]
    [TestOf(typeof(ServerPreferencesManager))]
    public sealed class CharacterCreationTest
    {
        [TestCase(1922730766, 1803840322)]
        [TestCase(1642676130, 1145912731)]
        public async Task CreateDeleteCreateTest(int clientSeed, int serverSeed)
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true, ClientSeed = clientSeed, ServerSeed = serverSeed });
            var server = pair.Server;
            var client = pair.Client;

            var clientNetManager = client.ResolveDependency<IClientNetManager>();
            var clientStateManager = client.ResolveDependency<IStateManager>();
            var clientPrefManager = client.ResolveDependency<IClientPreferencesManager>();

            var serverPrefManager = server.ResolveDependency<IServerPreferencesManager>();


            // Need to run them in sync to receive the messages.
            await pair.RunTicksSync(1);

            await PoolManager.WaitUntil(client, () => clientStateManager.CurrentState is LobbyState, 600);

            Assert.That(clientNetManager.ServerChannel, Is.Not.Null);

            var clientNetId = clientNetManager.ServerChannel.UserId;
            HumanoidCharacterProfile profile = null;

            await client.WaitAssertion(() =>
            {
                clientPrefManager.SelectCharacter(0);

                var clientCharacters = clientPrefManager.Preferences?.Characters;
                Assert.That(clientCharacters, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(clientCharacters, Has.Count.EqualTo(1));

                    Assert.That(clientStateManager.CurrentState, Is.TypeOf<LobbyState>());
                });

                profile = HumanoidCharacterProfile.Random();
                clientPrefManager.CreateCharacter(profile);

                clientCharacters = clientPrefManager.Preferences?.Characters;

                Assert.That(clientCharacters, Is.Not.Null);
                Assert.That(clientCharacters, Has.Count.EqualTo(2));
                Assert.That(clientCharacters[1].MemberwiseEquals(profile));
            });

            await PoolManager.WaitUntil(server, () => serverPrefManager.GetPreferences(clientNetId).Characters.Count == 2, maxTicks: 60);

            await server.WaitAssertion(() =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters;

                Assert.That(serverCharacters, Has.Count.EqualTo(2));
                Assert.That(serverCharacters[1].MemberwiseEquals(profile), Is.True,
                    () => $"Requested:\n{server.ResolveDependency<Robust.Shared.Serialization.Manager.ISerializationManager>().WriteValue(profile, alwaysWrite: true)}\nStored:\n{server.ResolveDependency<Robust.Shared.Serialization.Manager.ISerializationManager>().WriteValue((HumanoidCharacterProfile)serverCharacters[1], alwaysWrite: true)}");
            });

            await client.WaitAssertion(() =>
            {
                clientPrefManager.DeleteCharacter(1);

                var clientCharacters = clientPrefManager.Preferences?.Characters.Count;
                Assert.That(clientCharacters, Is.EqualTo(1));
            });

            await PoolManager.WaitUntil(server, () => serverPrefManager.GetPreferences(clientNetId).Characters.Count == 1, maxTicks: 60);

            await server.WaitAssertion(() =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters.Count;
                Assert.That(serverCharacters, Is.EqualTo(1));
            });

            await client.WaitIdleAsync();

            await client.WaitAssertion(() =>
            {
                profile = HumanoidCharacterProfile.Random();

                clientPrefManager.CreateCharacter(profile);

                var clientCharacters = clientPrefManager.Preferences?.Characters;

                Assert.That(clientCharacters, Is.Not.Null);
                Assert.That(clientCharacters, Has.Count.EqualTo(2));
                Assert.That(clientCharacters[1].MemberwiseEquals(profile));
            });

            await PoolManager.WaitUntil(server, () => serverPrefManager.GetPreferences(clientNetId).Characters.Count == 2, maxTicks: 120); //60->120 - Mono

            await server.WaitAssertion(() =>
            {
                var serverCharacters = serverPrefManager.GetPreferences(clientNetId).Characters;

                Assert.That(serverCharacters, Has.Count.EqualTo(2));
                Assert.That(serverCharacters[1].MemberwiseEquals(profile), Is.True,
                    () => $"Requested:\n{server.ResolveDependency<Robust.Shared.Serialization.Manager.ISerializationManager>().WriteValue(profile, alwaysWrite: true)}\nStored:\n{server.ResolveDependency<Robust.Shared.Serialization.Manager.ISerializationManager>().WriteValue((HumanoidCharacterProfile)serverCharacters[1], alwaysWrite: true)}");
            });
            await pair.CleanReturnAsync();
        }
    }
}
