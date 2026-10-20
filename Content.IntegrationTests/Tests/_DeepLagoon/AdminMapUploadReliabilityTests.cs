using System.Collections;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Administration.Managers;
using Content.Shared._DeepLagoon.Admin;
using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Content.Shared.GameTicking;
using Moq;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using ClientUpload = Content.Client._DeepLagoon.Admin.AdminMapUploadSystem;
using ServerUpload = Content.Server._DeepLagoon.Admin.AdminMapUploadSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class AdminMapUploadReliabilityTests
{
    [Test]
    public async Task PendingBytesSurviveActualClientDisconnectAndEntitySystemRestart()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var request = Guid.NewGuid();
        var bytes = new byte[AdminMapUploadLimits.ChunkBytes * 2];
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var net = pair.Client.ResolveDependency<IClientNetManager>();
        ClientUpload original = null;
        await pair.Client.WaitPost(() =>
        {
            original = pair.Client.EntMan.System<ClientUpload>();
            void Set(string field, object value) => typeof(ClientUpload).GetField(field, flags)!.SetValue(original, value);
            Set("_request", request);
            Set("_uploadBytes", bytes);
            Set("_uploadName", "resume.yml");
            Set("_uploadHash", Convert.ToHexString(SHA256.HashData(bytes)));
            Set("_receivedOffset", AdminMapUploadLimits.ChunkBytes);
            Set("_expires", pair.Client.ResolveDependency<IGameTiming>().RealTime + TimeSpan.FromMinutes(30));
            Set("_serverEndpoint", net.ServerChannel.RemoteEndPoint.ToString());
            new Content.Client._DeepLagoon.Admin.AdminMapUploadWindow().OpenCentered();
            net.ClientDisconnect("Simulated connection loss during upload");
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitPost(() =>
        {
            pair.Client.SetConnectTarget(pair.Server);
            net.ClientConnect(null, 0, null);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var resumed = pair.Client.EntMan.System<ClientUpload>();
            Assert.That(resumed, Is.Not.SameAs(original));
            Assert.That(resumed.IsBusy, Is.True);
            Assert.That(typeof(ClientUpload).GetField("_request", flags)!.GetValue(resumed), Is.EqualTo(request));
            Assert.That(typeof(ClientUpload).GetField("_uploadBytes", flags)!.GetValue(resumed), Is.SameAs(bytes));
            resumed.CancelPlacement();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReorderedTransferResumesAndRoundLibrarySupportsIndependentAdminPreviews()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<ServerUpload>();
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(session);
            var maps = entities.System<SharedMapSystem>();
            var destination = maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(1));
            using var writer = new StringWriter();
            Assert.That(entities.System<MapLoaderSystem>().TrySaveGrid(grid, writer), Is.True);
            entities.DeleteEntity(grid);
            var bytes = new byte[AdminMapUploadLimits.ChunkBytes * 4 + 17];
            Array.Fill(bytes, (byte) ' ');
            Encoding.UTF8.GetBytes(writer + "\n#").CopyTo(bytes, 0);
            var ghost = entities.SpawnEntity("MobObserver", new EntityCoordinates(destination, Vector2.Zero));
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            IDictionary State(string name) => (IDictionary) typeof(ServerUpload).GetField(name, flags)!.GetValue(system)!;
            void Call(string name, EntityEventArgs ev, ICommonSession sender = null) =>
                typeof(ServerUpload).GetMethod(name, flags)!.Invoke(system, [ev, new EntitySessionEventArgs(sender ?? session)]);
            var request = Guid.NewGuid();
            var begin = new AdminMapUploadBeginEvent(request, bytes.Length, "round-ship.yml", Convert.ToHexString(SHA256.HashData(bytes)));
            void Chunk(int index)
            {
                var offset = index * AdminMapUploadLimits.ChunkBytes;
                Call("OnChunk", new AdminMapUploadChunkEvent(request, offset,
                    bytes[offset..Math.Min(bytes.Length, offset + AdminMapUploadLimits.ChunkBytes)]));
            }
            Call("OnBegin", begin);
            var allocation = State("_uploads")[session.UserId];
            Chunk(3);
            Chunk(1);
            Chunk(1); // Duplicate data and lost acknowledgement.
            Chunk(4);
            Assert.That(State("_previews").Count, Is.Zero, "A gap must never produce a partial map");

            // Reconnect/re-possession: the authenticated account resumes into a new ghost.
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(ghost);
            system.Update(0);
            Assert.That(State("_uploads")[session.UserId], Is.SameAs(allocation));
            ghost = entities.SpawnEntity("MobObserver", new EntityCoordinates(destination, Vector2.One));
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
            Call("OnBegin", begin);
            Assert.That(State("_uploads")[session.UserId], Is.SameAs(allocation), "Resume must not reallocate or lose received chunks");
            Chunk(0);
            Chunk(2);
            Assert.That(State("_uploads").Count, Is.Zero);
            Assert.That(State("_library").Count, Is.EqualTo(1));
            Assert.That(State("_previews").Count, Is.EqualTo(1));
            Call("OnBegin", begin); // Lost ready response.
            Assert.That(State("_previews").Count, Is.EqualTo(1));
            var id = (Guid) State("_library").Keys.Cast<object>().Single();

            // A second authenticated administrator can use the same immutable bytes independently.
            var second = new Mock<ICommonSession>();
            second.SetupGet(s => s.UserId).Returns(new NetUserId(Guid.NewGuid()));
            second.SetupGet(s => s.AttachedEntity).Returns(ghost);
            second.SetupProperty(s => s.Channel, session.Channel);
            var adminField = typeof(ServerUpload).GetField("_admins", flags)!;
            var realAdmins = adminField.GetValue(system);
            var admins = new Mock<ISharedAdminManager>();
            admins.Setup(a => a.HasAdminFlag(It.IsAny<ICommonSession>(), AdminFlags.Mapping, false)).Returns(true);
            var secondRequest = Guid.NewGuid();
            try
            {
                adminField.SetValue(system, admins.Object);
                Call("OnLibraryPlace", new AdminMapLibraryPlaceEvent(secondRequest, id), second.Object);
                Call("OnLibraryPlace", new AdminMapLibraryPlaceEvent(secondRequest, id), second.Object);
                Assert.That(State("_previews").Count, Is.EqualTo(2), "Repeated selection is idempotent; each admin gets a private copy");
                Assert.That(State("_library").Count, Is.EqualTo(1));
                admins.Setup(a => a.HasAdminFlag(It.IsAny<ICommonSession>(), AdminFlags.Mapping, false)).Returns(false);
                Call("OnConfirm", new AdminMapPlacementConfirmEvent(secondRequest, Vector2.Zero, 0), second.Object);
                Assert.That(State("_previews").Count, Is.EqualTo(1), "Revoked permission must prevent placement");
            }
            finally
            {
                adminField.SetValue(system, realAdmins);
            }
            Call("OnCancel", new AdminMapPlacementCancelEvent(request));
            Assert.That(State("_library").Count, Is.EqualTo(1), "Cancelling a preview must retain the uploaded file");
            var fromLibrary = Guid.NewGuid();
            Call("OnLibraryPlace", new AdminMapLibraryPlaceEvent(fromLibrary, id));
            Call("OnConfirm", new AdminMapPlacementConfirmEvent(fromLibrary, new Vector2(10, 20), 0));
            Assert.That(State("_library").Count, Is.EqualTo(1), "Placing must retain the uploaded file");
            State("_cooldowns").Clear();
            request = Guid.NewGuid();
            Call("OnBegin", new AdminMapUploadBeginEvent(request, bytes.Length, "corrupt.yml", new string('0', 64)));
            for (var i = 0; i < 5; i++)
                Chunk(i);
            Assert.That(State("_uploads").Count, Is.Zero);
            Assert.That(State("_previews").Count, Is.Zero, "A checksum mismatch must not create a preview");
            Assert.That(State("_library").Count, Is.EqualTo(1), "Corrupt bytes must not enter the shared library");
            entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
            Assert.That(State("_library").Count, Is.Zero);
            Assert.That(State("_uploads").Count, Is.Zero);
            Assert.That(State("_previews").Count, Is.Zero);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
        });
        await pair.CleanReturnAsync();
    }
}
