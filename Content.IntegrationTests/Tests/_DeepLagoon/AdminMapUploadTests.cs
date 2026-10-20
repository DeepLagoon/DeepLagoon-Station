using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Content.Server.Administration.Managers;
using Content.Server.GameTicking;
using Content.Shared._DeepLagoon.Admin;
using Content.Shared.Administration;
using Content.Shared.Ghost;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Content.Client._DeepLagoon.Admin;
using Moq;
using Robust.Server.GameStates;
using Robust.Shared.Player;
using Robust.Client.Input;
using Robust.Client.Graphics;
using Robust.Shared.Timing;
using ClientUpload = Content.Client._DeepLagoon.Admin.AdminMapUploadSystem;
using ServerUpload = Content.Server._DeepLagoon.Admin.AdminMapUploadSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class AdminMapUploadTests
{
    private static async Task CleanReturnAsync(Content.IntegrationTests.Pair.TestPair pair)
    {
        // Detach while player data still exists, before the pool tears down the session.
        await pair.Server.WaitPost(() =>
        {
            foreach (var session in pair.Server.PlayerMan.Sessions.ToArray())
                pair.Server.PlayerMan.SetAttachedEntity(session, null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UploadWaitsForDialogAndStreamsChunksWithRenewedIdleTimeout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        byte[] bytes = [];
        await pair.Server.WaitPost(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(session);
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var map = maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(1));
            using var writer = new StringWriter();
            Assert.That(entities.System<MapLoaderSystem>().TrySaveGrid(grid, writer), Is.True);
            var yaml = Encoding.UTF8.GetBytes(writer + "\n#");
            bytes = new byte[32 * AdminMapUploadLimits.ChunkBytes + 17];
            Array.Fill(bytes, (byte) ' ');
            yaml.CopyTo(bytes, 0);
            entities.DeleteEntity(grid);
            var ghost = entities.SpawnEntity("MobObserver", new EntityCoordinates(map, Vector2.Zero));
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
        });
        await pair.RunTicksSync(30);

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var client = pair.Client.EntMan.System<ClientUpload>();
        var server = pair.Server.EntMan.System<ServerUpload>();
        var dialogsField = typeof(ClientUpload).GetField("_dialogs", flags)!;
        var clientTimingField = typeof(ClientUpload).GetField("_timing", flags)!;
        var serverTimingField = typeof(ServerUpload).GetField("_timing", flags)!;
        var originalDialogs = dialogsField.GetValue(client);
        var originalClientTiming = clientTimingField.GetValue(client);
        var originalServerTiming = serverTimingField.GetValue(server);
        var now = TimeSpan.Zero;
        var clock = new Mock<IGameTiming>();
        clock.SetupGet(timing => timing.RealTime).Returns(() => now);
        var picked = new TaskCompletionSource<Stream?>();
        var dialogs = new Mock<IFileDialogManager>();
        dialogs.Setup(dialog => dialog.OpenFile(It.IsAny<FileDialogFilters>(), FileAccess.Read, It.IsAny<FileShare?>()))
            .Returns(picked.Task);
        var received = new System.Collections.Generic.List<int>();
        string result = null;
        try
        {
            await pair.Server.WaitPost(() => serverTimingField.SetValue(server, clock.Object));
            await pair.Client.WaitAssertion(() =>
            {
                dialogsField.SetValue(client, dialogs.Object);
                clientTimingField.SetValue(client, clock.Object);
                client.PickAndUpload(message => result = message, (offset, _) =>
                {
                    received.Add(offset);
                    // Each acknowledgement takes four minutes: the whole transfer exceeds five.
                    now += TimeSpan.FromMinutes(4);
                });
                now += TimeSpan.FromMinutes(6);
                client.Update(0);
                Assert.That(result, Is.Null, "Waiting in the file dialog must not time out");
                Assert.That(typeof(ClientUpload).GetField("_expires", flags)!.GetValue(client), Is.Null);
                picked.SetResult(new MemoryStream(bytes));
            });
            await pair.RunTicksSync(250);
            await pair.Client.WaitAssertion(() =>
            {
                Assert.That(result, Is.EqualTo("dl-map-upload-preview"));
                Assert.That(client.HasPreview, Is.True);
                Assert.That(received.Last(), Is.EqualTo(bytes.Length));
                Assert.That(received.Distinct().Count(), Is.EqualTo(34), "Initial zero plus every chunk is acknowledged");
                Assert.That(now, Is.GreaterThan(TimeSpan.FromMinutes(100)));
                client.CancelPlacement();
            });
            await pair.RunTicksSync(10);
        }
        finally
        {
            await pair.Client.WaitPost(() =>
            {
                client.CancelPlacement();
                dialogsField.SetValue(client, originalDialogs);
                clientTimingField.SetValue(client, originalClientTiming);
            });
            await pair.Server.WaitPost(() => serverTimingField.SetValue(server, originalServerTiming));
        }
        await CleanReturnAsync(pair);
    }

    [Test]
    public async Task PrivatePreviewReplicatesOnlyForPlacementAndHandlesMoveRotateAndCancel()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var request = Guid.NewGuid();
        EntityUid destination = default;
        byte[] file = [];
        await pair.Server.WaitPost(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(session);
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            destination = maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(1));
            using var writer = new StringWriter();
            Assert.That(entities.System<MapLoaderSystem>().TrySaveGrid(grid, writer), Is.True);
            file = Encoding.UTF8.GetBytes(writer.ToString());
            entities.DeleteEntity(grid);
            var ghost = entities.SpawnEntity("MobObserver",
                new EntityCoordinates(destination, new Vector2(25, 30)));
            Assert.That(entities.GetComponent<GhostComponent>(ghost).CanGhostInteract, Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitPost(() => typeof(ClientUpload).GetField("_request", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pair.Client.EntMan.System<ClientUpload>(), request));
        await pair.Server.WaitPost(() =>
        {
            var system = pair.Server.EntMan.System<ServerUpload>();
            var sender = new EntitySessionEventArgs(pair.Server.PlayerMan.Sessions.Single());
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(ServerUpload).GetMethod("OnBegin", flags)!.Invoke(system, [new AdminMapUploadBeginEvent(request, file.Length), sender]);
            typeof(ServerUpload).GetMethod("OnChunk", flags)!.Invoke(system, [new AdminMapUploadChunkEvent(request, 0, file), sender]);
        });
        await pair.RunTicksSync(60);
        await pair.Client.WaitAssertion(() =>
        {
            var system = pair.Client.EntMan.System<ClientUpload>();
            Assert.That(system.HasPreview, Is.True);
            Assert.That(system.CanConfirmPlacement, Is.True, "The private paused map must reach the uploader's viewport");
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var key = typeof(ClientUpload).GetMethod("PlacementKey", flags)!;
            var update = typeof(ClientUpload).GetMethod("UpdatePlacement", flags)!;
            var eye = pair.Client.ResolveDependency<IEyeManager>();
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            ui.KeyboardFocused?.ReleaseKeyboardFocus();
            ui.ClearWindows();
            var start = system.PlacementPosition;
            var right = new KeyEventArgs(Keyboard.Key.Right, false, false, true, false, false, 0);
            key.Invoke(system, [right, KeyEventType.Down]);
            Assert.That(right.Handled, Is.True);
            Assert.That(Vector2.Distance(system.PlacementPosition,
                start + (-eye.CurrentEye.Rotation).RotateVec(Vector2.UnitX)), Is.LessThan(0.001));

            foreach (var (arrow, direction) in new[]
                     {
                         (Keyboard.Key.Right, Vector2.UnitX), (Keyboard.Key.Left, -Vector2.UnitX),
                         (Keyboard.Key.Up, Vector2.UnitY), (Keyboard.Key.Down, -Vector2.UnitY)
                     })
            {
                foreach (var type in new[] { KeyEventType.Down, KeyEventType.Repeat, KeyEventType.Up })
                {
                    var before = system.PlacementPosition;
                    var fast = new KeyEventArgs(arrow, type == KeyEventType.Repeat, false, true, true, false, 0);
                    key.Invoke(system, [fast, type]);
                    Assert.That(fast.Handled, Is.True);
                    var expected = type == KeyEventType.Up ? before :
                        before + (-eye.CurrentEye.Rotation).RotateVec(direction * 10);
                    Assert.That(Vector2.Distance(system.PlacementPosition, expected), Is.LessThan(0.001));
                }
            }

            // Ghost movement must still reach the normal input bindings, including with placement modifiers held.
            var placement = system.PlacementPosition;
            foreach (var movement in new[] { Keyboard.Key.W, Keyboard.Key.A, Keyboard.Key.S, Keyboard.Key.D,
                         Keyboard.Key.Left, Keyboard.Key.Right, Keyboard.Key.Up, Keyboard.Key.Down })
            foreach (var control in new[] { false, true })
            foreach (var shift in new[] { false, true })
            foreach (var type in new[] { KeyEventType.Down, KeyEventType.Repeat, KeyEventType.Up })
            {
                if (control && movement is Keyboard.Key.Left or Keyboard.Key.Right or Keyboard.Key.Up or Keyboard.Key.Down)
                    continue;
                var movementKey = new KeyEventArgs(movement, type == KeyEventType.Repeat, false, control, shift, false, 0);
                key.Invoke(system, [movementKey, type]);
                Assert.That(movementKey.Handled, Is.False, $"Ghost movement {movement}, Ctrl={control}, Shift={shift}, {type}");
                Assert.That(system.PlacementPosition, Is.EqualTo(placement));
            }

            var inputField = typeof(ClientUpload).GetField("_input", flags)!;
            var original = inputField.GetValue(system);
            // The dummy ticker does not mount a gameplay screen. Supply an actual viewport for pointer routing.
            var previousViewport = eye.MainViewport;
            using var viewport = new Robust.Client.UserInterface.CustomControls.ViewportContainer
                { MouseFilter = Control.MouseFilterMode.Stop };
            ui.WindowRoot.AddChild(viewport);
            viewport.Measure(new Vector2(640, 480));
            viewport.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(640, 480)));
            eye.MainViewport = viewport;
            var mouse = new ScreenCoordinates(viewport.GlobalPixelPosition + viewport.PixelSize / 2, viewport.Window!.Id);
            var input = new Mock<IInputManager>();
            input.SetupGet(manager => manager.MouseScreenPosition).Returns(() => mouse);
            inputField.SetValue(system, input.Object);
            try
            {
                var middle = new KeyEventArgs(Keyboard.Key.MouseMiddle, false, false, false, false, false, 0);
                key.Invoke(system, [middle, KeyEventType.Down]);
                Assert.That(middle.Handled, Is.True,
                    $"Pointer hit {ui.MouseGetControl(mouse)?.GetType().Name}; viewport {viewport.GetType().Name}, visible {viewport.VisibleInTree}, size {viewport.PixelSize}");
                mouse = new ScreenCoordinates(mouse.Position + new Vector2(100, 0), mouse.Window);
                update.Invoke(system, null);
                Assert.That(system.PlacementRotation, Is.EqualTo(1).Within(0.001));
                key.Invoke(system, [new KeyEventArgs(Keyboard.Key.MouseMiddle, false, false, false, false, false, 0), KeyEventType.Up]);
                mouse = new ScreenCoordinates(mouse.Position + new Vector2(100, 0), mouse.Window);
                update.Invoke(system, null);
                Assert.That(system.PlacementRotation, Is.EqualTo(1).Within(0.001), "Releasing MMB stops rotation");
            }
            finally
            {
                inputField.SetValue(system, original);
                eye.MainViewport = previousViewport;
            }
            key.Invoke(system, [new KeyEventArgs(Keyboard.Key.Escape, false, false, false, false, false, 0), KeyEventType.Down]);
            Assert.That(system.HasPreview, Is.False);
        });
        await pair.RunTicksSync(10);
        await pair.Server.WaitAssertion(() =>
        {
            var query = pair.Server.EntMan.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (query.MoveNext(out _, out _, out var xform))
                Assert.That(xform.MapUid, Is.Not.EqualTo(destination), "Cancelled preview must not create a live grid");
        });
        await CleanReturnAsync(pair);
    }

    [TestCase("AdminObserver")]
    [TestCase("MobObserver")]
    public async Task PickButtonOpensDialogForMappingGhost(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitPost(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(session);
            var map = pair.Server.EntMan.System<SharedMapSystem>().CreateMap();
            var ghost = pair.Server.EntMan.SpawnEntity(prototype,
                new EntityCoordinates(map, Vector2.Zero));
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var system = pair.Client.EntMan.System<ClientUpload>();
            Assert.That(system.CanUpload, Is.True, "The server-authorized admin ghost must pass the client gate");
            var dialogs = new Mock<IFileDialogManager>();
            dialogs.Setup(dialog => dialog.OpenFile(It.IsAny<FileDialogFilters>(), FileAccess.Read, It.IsAny<FileShare?>()))
                .Returns(Task.FromResult<Stream?>(null));
            var field = typeof(ClientUpload).GetField("_dialogs", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = field.GetValue(system);
            field.SetValue(system, dialogs.Object);
            try
            {
                using var window = new AdminMapUploadWindow();
                window.OpenCentered();
                window.Measure(new Vector2(1000, 600));
                window.Arrange(UIBox2.FromDimensions(Vector2.Zero, window.DesiredSize));
                var button = window.Contents.Children.Single().Children.OfType<Button>().Single();
                var coordinates = new ScreenCoordinates(button.GlobalPixelPosition + button.PixelSize / 2, button.Window!.Id);
                var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
                Assert.That(ui.MouseGetControl(coordinates), Is.SameAs(button));
                foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
                    ui.GetType().GetMethod(state == BoundKeyState.Down ? "KeyBindDown" : "KeyBindUp")!
                        .Invoke(ui, [new BoundKeyEventArgs(EngineKeyFunctions.UIClick, state, coordinates, true)]);
                dialogs.Verify(dialog => dialog.OpenFile(It.IsAny<FileDialogFilters>(), FileAccess.Read, It.IsAny<FileShare?>()), Times.Once);
                Assert.That(button.Disabled, Is.False, "Cancel must re-enable the button");
            }
            finally
            {
                field.SetValue(system, original);
            }
        });
        await CleanReturnAsync(pair);
    }

    [Test]
    public async Task PickButtonReceivesPointerClickAndShowsPermissionError()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        AdminMapUploadWindow window = null!;
        Button button = null!;
        await pair.Client.WaitAssertion(() =>
        {
            window = new AdminMapUploadWindow();
            window.OpenCentered();
            button = window.Contents.Children.Single().Children.OfType<Button>().Single();
        });
        await pair.Client.WaitRunTicks(3);
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var position = button.GlobalPixelPosition + button.PixelSize / 2;
            var coordinates = new ScreenCoordinates(position, button.Window!.Id);
            Assert.That(ui.MouseGetControl(coordinates), Is.SameAs(button), "The visible button must own its hit area");
            foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
            {
                ui.GetType().GetMethod(state == BoundKeyState.Down ? "KeyBindDown" : "KeyBindUp")!
                    .Invoke(ui, [new BoundKeyEventArgs(EngineKeyFunctions.UIClick, state, coordinates, true)]);
            }
            var status = window.Contents.Children.Single().Children.OfType<Label>().Single();
            Assert.That(status.Text, Is.EqualTo(Robust.Shared.Localization.Loc.GetString("dl-map-upload-denied")));
            window.Close();
        });
        await CleanReturnAsync(pair);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GridAndMultiGridMapAreCenteredAtGhost(bool wholeMap)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var session = pair.Server.PlayerMan.Sessions.Single();
        var admins = pair.Server.ResolveDependency<IAdminManager>();
        await pair.Server.WaitPost(() => admins.PromoteHost(session));
        await PoolManager.WaitUntil(pair.Server, () => Task.FromResult(admins.HasAdminFlag(session, AdminFlags.Mapping)));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transform = entities.System<SharedTransformSystem>();
            var loader = entities.System<MapLoaderSystem>();
            var source = maps.CreateMap(out var sourceId, runMapInit: false);
            var first = maps.CreateGridEntity(sourceId);
            maps.SetTile(first, new Vector2i(10, 20), new Tile(1));
            transform.SetLocalPosition(first, new Vector2(70, -20));
            if (wholeMap)
            {
                var second = maps.CreateGridEntity(sourceId);
                maps.SetTile(second, Vector2i.Zero, new Tile(1));
                transform.SetLocalPosition(second, new Vector2(-30, 25));
            }

            using var writer = new StringWriter();
            Assert.That(wholeMap ? loader.TrySaveMap(source, writer) : loader.TrySaveGrid(first, writer), Is.True);
            entities.DeleteEntity(source);
            var destination = maps.CreateMap(out var destinationId);
            var target = new Vector2(345, -678);
            var ghost = entities.SpawnEntity(GameTicker.AdminObserverPrototypeName, new EntityCoordinates(destination, target));
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
            var beforeMaps = maps.GetAllMapIds().Count();
            var system = entities.System<ServerUpload>();
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var sender = new EntitySessionEventArgs(session);
            var cooldowns = (System.Collections.IDictionary) typeof(ServerUpload).GetField("_cooldowns", flags)!.GetValue(system)!;
            Guid Prepare(string yaml)
            {
                cooldowns.Clear();
                var id = Guid.NewGuid();
                var bytes = Encoding.UTF8.GetBytes(yaml);
                typeof(ServerUpload).GetMethod("OnBegin", flags)!.Invoke(system, [new AdminMapUploadBeginEvent(id, bytes.Length), sender]);
                typeof(ServerUpload).GetMethod("OnChunk", flags)!.Invoke(system, [new AdminMapUploadChunkEvent(id, 0, bytes), sender]);
                return id;
            }
            var request = Prepare(writer.ToString());
            typeof(ServerUpload).GetMethod("OnConfirm", flags)!.Invoke(system,
                [new AdminMapPlacementConfirmEvent(request, target, 0), sender]);
            var grids = entities.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            Box2? bounds = null;
            var count = 0;
            while (grids.MoveNext(out var uid, out var grid, out var xform))
            {
                if (xform.MapID != destinationId)
                    continue;
                count++;
                var box = transform.GetWorldMatrix(uid).TransformBox(grid.LocalAABB);
                bounds = bounds?.Union(box) ?? box;
            }
            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(wholeMap ? 2 : 1));
                Assert.That(Vector2.Distance(bounds!.Value.Center, target), Is.LessThan(0.001f));
                Assert.That(maps.GetAllMapIds().Count(), Is.EqualTo(beforeMaps), "Temporary map must be removed");
            });

            var empty = maps.CreateMap(out _, runMapInit: false);
            using var emptyWriter = new StringWriter();
            Assert.That(loader.TrySaveMap(empty, emptyWriter), Is.True);
            entities.DeleteEntity(empty);
            Prepare(emptyWriter.ToString());
            Assert.That(maps.GetAllMapIds().Count(), Is.EqualTo(beforeMaps));
            entities.DeleteEntity(destination);
        });
        await CleanReturnAsync(pair);
    }

    [Test]
    public async Task UploadRejectsUnauthorizedInvalidAndRepeatedTransfers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var server = pair.Server;
        var session = server.PlayerMan.Sessions.Single();
        var admins = server.ResolveDependency<IAdminManager>();
        await server.WaitPost(() => admins.PromoteHost(session));
        await PoolManager.WaitUntil(server, () => Task.FromResult(admins.HasAdminFlag(session, AdminFlags.Mapping)));
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var system = entities.System<ServerUpload>();
            var map = maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(1));
            using var writer = new StringWriter();
            Assert.That(entities.System<MapLoaderSystem>().TrySaveGrid(grid, writer), Is.True);
            var bytes = Encoding.UTF8.GetBytes(writer.ToString());
            // Exercise the exact size limit through the real chunk receiver using a valid YAML comment.
            var padded = new byte[AdminMapUploadLimits.MaxBytes];
            bytes.CopyTo(padded, 0);
            padded[bytes.Length] = (byte) '\n';
            padded[bytes.Length + 1] = (byte) '#';
            Array.Fill(padded, (byte) ' ', bytes.Length + 2, padded.Length - bytes.Length - 2);
            bytes = padded;
            entities.DeleteEntity(grid);
            var ghost = entities.SpawnEntity("MobObserver", new EntityCoordinates(map, new Vector2(100, 200)));
            Assert.That(entities.GetComponent<GhostComponent>(ghost).CanGhostInteract, Is.False);
            server.PlayerMan.SetAttachedEntity(session, ghost);

            var sender = new EntitySessionEventArgs(session);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Begin(Guid id, int length) => typeof(ServerUpload).GetMethod("OnBegin", flags)!.Invoke(system, [new AdminMapUploadBeginEvent(id, length), sender]);
            void Chunk(Guid id, int offset, byte[] chunk) => typeof(ServerUpload).GetMethod("OnChunk", flags)!.Invoke(system, [new AdminMapUploadChunkEvent(id, offset, chunk), sender]);
            var uploads = (System.Collections.IDictionary)typeof(ServerUpload).GetField("_uploads", flags)!.GetValue(system)!;
            var cooldowns = (System.Collections.IDictionary)typeof(ServerUpload).GetField("_cooldowns", flags)!.GetValue(system)!;
            int GridCount()
            {
                var count = 0;
                var query = entities.AllEntityQueryEnumerator<MapGridComponent>();
                while (query.MoveNext(out _, out _)) count++;
                return count;
            }
            var before = GridCount();

            // Mapping alone is sufficient; Admin or Query must not be required.
            var data = admins.GetAdminData(session)!;
            data.Flags = AdminFlags.Mapping;
            Assert.That(system.CanUpload(session), Is.True);
            data.Flags = AdminFlags.Admin;
            Assert.That(system.CanUpload(session), Is.False);
            data.Flags = AdminFlags.Mapping;
            Begin(Guid.NewGuid(), AdminMapUploadLimits.MaxBytes + 1);
            Assert.That(uploads.Count, Is.Zero);
            var id = Guid.NewGuid();
            Begin(id, bytes.Length);
            Assert.That(uploads.Count, Is.EqualTo(1));
            // Rights revoked between begin and data: no allocation or spawn remains.
            admins.DeAdmin(session);
            Assert.That(system.CanUpload(session), Is.False);
            Chunk(id, 0, bytes[..AdminMapUploadLimits.ChunkBytes]);
            Assert.That(uploads.Count, Is.Zero);
            Begin(Guid.NewGuid(), bytes.Length);
            Assert.That(uploads.Count, Is.Zero);
            admins.ReAdmin(session);
            cooldowns.Clear();

            // A gap aborts the transfer and cannot be repaired by replaying its last chunk.
            id = Guid.NewGuid();
            Begin(id, bytes.Length);
            Chunk(id, 1, bytes[..AdminMapUploadLimits.ChunkBytes]);
            Chunk(id, 0, bytes[..AdminMapUploadLimits.ChunkBytes]);
            Assert.That(GridCount(), Is.EqualTo(before));
            cooldowns.Clear();

            // A complete transfer prepares a paused private preview, without a live grid or MapInit.
            id = Guid.NewGuid();
            Begin(id, bytes.Length);
            for (var offset = 0; offset < bytes.Length; offset += AdminMapUploadLimits.ChunkBytes)
                Chunk(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminMapUploadLimits.ChunkBytes)]);
            Chunk(id, 0, bytes[..AdminMapUploadLimits.ChunkBytes]);
            Assert.That(GridCount(), Is.EqualTo(before + 1));
            Assert.That(uploads.Count, Is.Zero);
            var previews = (System.Collections.IDictionary) typeof(ServerUpload).GetField("_previews", flags)!.GetValue(system)!;
            Assert.That(previews.Count, Is.EqualTo(1));
            var preview = previews[session.UserId]!;
            var staging = (EntityUid) preview.GetType().GetProperty("Stage")!.GetValue(preview)!;
            var placedGrid = entities.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();
            EntityUid previewGrid = default;
            while (placedGrid.MoveNext(out var uid, out _, out var gridTransform))
            {
                Assert.That(gridTransform.MapUid, Is.Not.EqualTo(map), "No live grid before confirmation");
                if (gridTransform.MapUid == staging)
                    previewGrid = uid;
            }
            Assert.That(maps.IsInitialized(staging), Is.False);
            Assert.That(maps.IsPaused(staging), Is.True);
            var pvs = entities.System<PvsOverrideSystem>();
            var forceSend = (System.Collections.Generic.HashSet<EntityUid>) typeof(PvsOverrideSystem)
                .GetField("ForceSend", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pvs)!;
            Assert.That(forceSend.Contains(staging), Is.False);
            Assert.That(forceSend.Contains(previewGrid), Is.False);
            var sessionOverrides = (System.Collections.Generic.Dictionary<ICommonSession, System.Collections.Generic.HashSet<EntityUid>>)
                typeof(PvsOverrideSystem).GetField("SessionOverrides", flags)!.GetValue(pvs)!;
            Assert.That(sessionOverrides.Where(entry => entry.Value.Contains(staging)).Select(entry => entry.Key),
                Is.EqualTo(new[] { session }), "Only the uploader gets the preview map");

            var target = new Vector2(150, -210);
            void Confirm(Guid request) => typeof(ServerUpload).GetMethod("OnConfirm", flags)!.Invoke(system,
                [new AdminMapPlacementConfirmEvent(request, target, MathF.PI / 2), sender]);
            Confirm(Guid.NewGuid());
            Assert.That(previews.Count, Is.EqualTo(1), "Wrong request must not consume another preview");
            Confirm(id);
            Confirm(id);
            Assert.That(previews.Count, Is.Zero);
            Assert.That(entities.Deleted(staging), Is.True);
            Assert.That(GridCount(), Is.EqualTo(before + 1), "Repeated confirmation must not spawn twice");
            var finalTransform = entities.GetComponent<TransformComponent>(previewGrid);
            var transformSystem = entities.System<SharedTransformSystem>();
            Assert.That(finalTransform.MapUid, Is.EqualTo(map));
            Assert.That(forceSend.Contains(previewGrid), Is.True, "Committed grid resumes normal replication");
            var finalBounds = transformSystem.GetWorldMatrix(previewGrid)
                .TransformBox(entities.GetComponent<MapGridComponent>(previewGrid).LocalAABB);
            Assert.That(Vector2.Distance(finalBounds.Center, target), Is.LessThan(0.001f));
            Assert.That(finalTransform.LocalRotation.Theta, Is.EqualTo(MathF.PI / 2).Within(0.001));

            // Cancellation deletes the preview and all its entities without placing them.
            cooldowns.Clear();
            id = Guid.NewGuid();
            var smallBytes = Encoding.UTF8.GetBytes(writer.ToString());
            Begin(id, smallBytes.Length);
            Chunk(id, 0, smallBytes);
            Assert.That(previews.Count, Is.EqualTo(1));
            typeof(ServerUpload).GetMethod("OnCancel", flags)!.Invoke(system, [new AdminMapPlacementCancelEvent(id), sender]);
            Assert.That(previews.Count, Is.Zero);
            Assert.That(GridCount(), Is.EqualTo(before + 1));

            // Losing Mapping during placement also prevents commit and removes the staged grid.
            cooldowns.Clear();
            id = Guid.NewGuid();
            Begin(id, smallBytes.Length);
            Chunk(id, 0, smallBytes);
            Assert.That(previews.Count, Is.EqualTo(1));
            admins.DeAdmin(session);
            Confirm(id);
            Assert.That(previews.Count, Is.Zero);
            Assert.That(GridCount(), Is.EqualTo(before + 1));
            admins.ReAdmin(session);

            // Mapping still does not permit uploads from a living character.
            var body = entities.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
            server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(system.CanUpload(session), Is.False);
        });
        await CleanReturnAsync(pair);
    }
}
