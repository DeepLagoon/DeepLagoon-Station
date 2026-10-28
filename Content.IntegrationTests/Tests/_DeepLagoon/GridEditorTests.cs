#nullable enable
using Robust.Shared.EntitySerialization.Systems;
using Content.Server._DeepLagoon.Mapping;
using System.Linq;
using System.Collections;
using Robust.Shared.Player;
using Robust.Shared.Enums;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Server.Administration.Managers;
using Content.Server.Decals;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Content.Shared.Decals;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Maths;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using ClientEditor = Content.Client._DeepLagoon.Mapping.GridEditorSystem;
using ServerEditor = Content.Server._DeepLagoon.Mapping.GridEditorSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GridEditorTests
{
    private static object DraftFor(ServerEditor editor, ICommonSession player, EntityUid? grid = null)
    {
        var drafts = (IDictionary) typeof(ServerEditor).GetField("_drafts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        return drafts.Keys.Cast<(ICommonSession Player, Guid Token)>().Where(key => key.Player == player)
            .Select(key => drafts[key]!).Single(draft => grid == null || (EntityUid) draft.GetType().GetField("Grid")!.GetValue(draft)! == grid);
    }

    private sealed class TabField(string name)
    {
        private object Tab(object editor) => typeof(ClientEditor).GetField("_tab", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        public object? GetValue(object editor) { var tab = Tab(editor); return tab.GetType().GetField(name)!.GetValue(tab); }
        public void SetValue(object editor, object? value) { var tab = Tab(editor); tab.GetType().GetField(name)!.SetValue(tab, value); }
    }

    [Test]
    public async Task GridTabsKeepIndependentDraftsAndPipeColors()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var grids = new EntityUid[2];
        var pipes = new EntityUid[2];
        var tokens = new Guid[2];
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId, runMapInit: true);
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var editor = entities.System<ServerEditor>();
            for (var i = 0; i < grids.Length; i++)
            {
                var grid = maps.CreateGridEntity(mapId); grids[i] = grid.Owner;
                maps.SetTile(grid, grid.Comp, Vector2i.Zero, new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
                pipes[i] = entities.SpawnEntity("GasPipeStraight", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
                editor.Open(player, grid);
                var draft = DraftFor(editor, player, grid);
                tokens[i] = (Guid) draft.GetType().GetField("Token")!.GetValue(draft)!;
            }
            editor.Open(player, grids[0]);
            Assert.That((Guid) DraftFor(editor, player, grids[0]).GetType().GetField("Token")!.GetValue(DraftFor(editor, player, grids[0]))!, Is.EqualTo(tokens[0]));
        });
        await pair.RunTicksSync(25);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var tabs = (IDictionary) typeof(ClientEditor).GetField("_tabs", flags)!.GetValue(editor)!;
            Assert.That(tabs.Count, Is.EqualTo(2));
            Assert.That(editor.State!.Token, Is.EqualTo(tokens[0]));
            // Upload two independent chunks while the other grid is active.
            void Send(int tab, string action, object payload) => typeof(ClientEditor).GetMethod("TabAction", flags)!.Invoke(editor,
                new object[] { action, JsonSerializer.Serialize(payload) });
            for (var i = 0; i < 2; i++) Send(i, "applyStart", new { tabToken = tokens[i], id = "color" + i, count = 1, chunks = 1 });
            for (var i = 1; i >= 0; i--)
                Send(i, "applyChunk", new { tabToken = tokens[i], id = "color" + i, index = 0, items = new[] { new
                {
                    id = tabs[tokens[i]]!.GetType().GetField("State")!.GetValue(tabs[tokens[i]]) is GridEditorStateEvent state
                        ? state.Items.Single(item => item.Prototype == "GasPipeStraight").Id : "",
                    kind = "entity", prototype = "GasPipeStraight", x = .5, y = .5, rotation = 0, anchored = true,
                    color = i == 0 ? "#FF0000FF" : "#0000FFFF"
                } } });
        });
        await pair.RunTicksSync(40);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<Content.Server.Atmos.Piping.Components.AtmosPipeColorComponent>(pipes[0]).Color, Is.EqualTo(Color.Red));
            Assert.That(entities.GetComponent<Content.Server.Atmos.Piping.Components.AtmosPipeColorComponent>(pipes[1]).Color, Is.EqualTo(Color.Blue));
            var editor = entities.System<ServerEditor>();
            var player = pair.Server.PlayerMan.Sessions.Single();
            var yaml = editor.Export(player, tokens[0], false, out var error);
            Assert.That(yaml, Is.Not.Null, error);
            Assert.That(yaml, Does.Contain("AtmosPipeColor"));
            typeof(ServerEditor).GetMethod("CloseEditor", flags)!.Invoke(editor, new object?[] { player, tokens[0] });
            Assert.That(DraftFor(editor, player, grids[1]), Is.Not.Null);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            Assert.That(((IDictionary) typeof(ClientEditor).GetField("_tabs", flags)!.GetValue(editor)!).Count, Is.EqualTo(1));
            Assert.That(editor.State!.Token, Is.EqualTo(tokens[1]));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExportFailureReportsComponentEntityAndGridLocalCoordinates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTiles(grid, grid.Comp, new() { (new(2, -2), new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId)) });
            var transforms = entities.System<SharedTransformSystem>();
            transforms.SetLocalPosition(grid, new Vector2(42, -17));
            transforms.SetLocalRotation(grid, Angle.FromDegrees(30));
            var chair = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(2.5f, -1.5f)));
            entities.AddComponent<GridEditorBrokenExportComponent>(chair);
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var editor = entities.System<ServerEditor>();
            editor.Open(player, grid);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var draft = DraftFor(editor, player);
            var token = (Guid) draft.GetType().GetField("Token")!.GetValue(draft)!;
            object?[] args = { player, token, false, "", null };
            var export = typeof(ServerEditor).GetMethods(flags).Single(method => method.Name == "Export" && method.GetParameters().Length == 5);
            var failureLevel = pair.ServerLogHandler.FailureLevel;
            try
            {
                pair.ServerLogHandler.FailureLevel = null;
                Assert.That(export.Invoke(editor, args), Is.Null, "A failing object must never produce a partial download");
            }
            finally
            {
                pair.ServerLogHandler.FailureLevel = failureLevel;
            }
            var issue = (GridEditorExportIssue) args[4]!;
            Assert.That(issue.Id, Is.EqualTo("e:" + entities.GetNetEntity(chair)));
            Assert.That(issue.Prototype, Is.EqualTo("ChairWood"));
            Assert.That(issue.Component, Is.EqualTo("GridEditorBrokenExport"));
            Assert.That(issue.Located, Is.True);
            Assert.That(issue.X, Is.EqualTo(2.5f).Within(.001));
            Assert.That(issue.Y, Is.EqualTo(-1.5f).Within(.001));
            Assert.That(issue.Message, Does.Contain("GridEditorBrokenExportValue"));
            Assert.That(entities.EntityExists(chair), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(chair).LocalPosition, Is.EqualTo(new Vector2(2.5f, -1.5f)));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoutedCablesAndPipesStayInstalledAndConnectedAfterApplyAndMove()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default;
        var prototypes = new[] { "CableHV", "CableMV", "CableApcExtension", "GasPipeStraight", "DisposalPipe" };
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId, runMapInit: true);
            var created = map.CreateGridEntity(mapId); grid = created.Owner;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            map.SetTiles(created, created.Comp, Enumerable.Range(0, 5).SelectMany(y => Enumerable.Range(0, 5).Select(x => (new Vector2i(x, y), floor))).ToList());
            entities.System<SharedTransformSystem>().SetLocalPosition(grid, new Vector2(-12.5f, 37.25f));
            entities.System<SharedTransformSystem>().SetLocalRotation(grid, Angle.FromDegrees(30));
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            Assert.That(entities.System<ServerEditor>().Open(player, grid), Does.Contain("открыт"));
        });
        await pair.RunTicksSync(25);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var installed = new Dictionary<string, EntityUid[]>();
        async Task Send(bool move)
        {
            await pair.Client.WaitAssertion(() =>
            {
                var editor = pair.Client.EntMan.System<ClientEditor>();
                var action = typeof(ClientEditor).GetMethod("Action", flags)!;
                var id = Guid.NewGuid().ToString();
                var items = move ? editor.State!.Items.Where(item => item.Kind == "entity" && prototypes.Contains(item.Prototype))
                    .Select(item => new GridEditorItem { Id = item.Id, Kind = item.Kind, Prototype = item.Prototype,
                        X = item.X + 1, Y = item.Y, Rotation = item.Rotation, Anchored = true }).ToList()
                    : prototypes.SelectMany((prototype, y) => Enumerable.Range(0, 3).Select(x => new GridEditorItem
                    { Id = $"new_route_{x}_{y}", Kind = "entity", Prototype = prototype, X = x + .5f, Y = y + .5f, Rotation = y < 3 ? 0 : 90, Anchored = true })).ToList();
                action.Invoke(editor, new object[] { "applyStart", JsonSerializer.Serialize(new { id, count = items.Count, chunks = 1 }) });
                action.Invoke(editor, new object[] { "applyChunk", JsonSerializer.Serialize(new { id, index = 0,
                    items = items.Select(item => new { id = item.Id, kind = item.Kind, prototype = item.Prototype, x = item.X, y = item.Y, rotation = item.Rotation, anchored = item.Anchored }) }) });
            });
            await pair.RunTicksSync(30);
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var children = entities.GetComponent<TransformComponent>(grid).ChildEnumerator;
                var placed = new List<EntityUid>();
                while (children.MoveNext(out var uid)) placed.Add(uid);
                Assert.That(placed.Where(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID.Contains("Stack") == true), Is.Empty, "Installed cables must not turn into loose coils");
                foreach (var prototype in prototypes)
                {
                    var route = placed.Where(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototype)
                        .OrderBy(uid => entities.GetComponent<TransformComponent>(uid).LocalPosition.X).ToArray();
                    Assert.That(route.Length, Is.EqualTo(3), prototype);
                    if (move) Assert.That(route, Is.EquivalentTo(installed[prototype]), "Moving a route must preserve its entities and component values");
                    else installed[prototype] = route;
                    for (var x = 0; x < 3; x++)
                    {
                        var transform = entities.GetComponent<TransformComponent>(route[x]);
                        Assert.That(transform.Anchored, Is.True, prototype);
                        Assert.That(transform.ParentUid, Is.EqualTo(grid));
                        Assert.That(transform.LocalPosition.X, Is.EqualTo(x + .5f + (move ? 1 : 0)).Within(.001f));
                    }
                    if (prototype.StartsWith("Cable"))
                    {
                        var nodes = route.Select(uid => entities.GetComponent<Content.Shared.NodeContainer.NodeContainerComponent>(uid).Nodes["power"]).ToArray();
                        Assert.That(nodes[0].NodeGroup, Is.Not.Null, prototype);
                        Assert.That(nodes.Select(node => node.NodeGroup).Distinct().Count(), Is.EqualTo(1), prototype);
                    }
                }
            });
        }
        await Send(false);
        await Send(true);
        EntityUid cut = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var children = entities.GetComponent<TransformComponent>(grid).ChildEnumerator;
            while (children.MoveNext(out var uid))
                if (entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "CableHV") { cut = uid; break; }
            Assert.That(cut, Is.Not.EqualTo(EntityUid.Invalid));
            entities.System<SharedTransformSystem>().Unanchor(cut);
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.EntityExists(cut), Is.False, "Ordinary unanchoring must still cut a cable");
            var children = entities.GetComponent<TransformComponent>(grid).ChildEnumerator;
            var coils = 0;
            while (children.MoveNext(out var uid))
                if (entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "CableHVStack1") coils++;
            Assert.That(coils, Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DraftAndMapExportsPreserveComponentValuesAndContainersWithoutApplyingDraft()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var frozenPlan = "";
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId, runMapInit: true);
            var grid = map.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            map.SetTiles(grid, grid.Comp, new() { (new(0, 0), floor), (new(1, 0), floor) });
            entities.System<MetaDataSystem>().SetEntityName(grid, "Грид с настройками");
            var chair = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            entities.System<MetaDataSystem>().SetEntityName(chair, "Особый стул");
            var vendor = entities.SpawnEntity("VendingMachineTankDispenserEVAPOI", new EntityCoordinates(grid, new Vector2(1.5f, .5f)));
            var stock = entities.GetComponent<Content.Shared.VendingMachines.VendingMachineComponent>(vendor);
            foreach (var (inventory, type, amount) in new[]
            {
                (stock.Inventory, Content.Shared.VendingMachines.InventoryType.Regular, 3u),
                (stock.EmaggedInventory, Content.Shared.VendingMachines.InventoryType.Emagged, 2u),
                (stock.ContrabandInventory, Content.Shared.VendingMachines.InventoryType.Contraband, 0u)
            })
            {
                inventory.Clear();
                inventory.Add("Paper", new(type, "Paper", amount));
            }
            var settings = entities.AddComponent<Content.Shared.Lock.LockComponent>(chair);
            foreach (var (field, value) in new[] { ("Locked", false), ("LockOnClick", true), ("UnlockOnClick", false), ("BreakOnAccessBreaker", false) })
                typeof(Content.Shared.Lock.LockComponent).GetField(field)!.SetValue(settings, value);
            var paper = entities.SpawnEntity("Paper", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            entities.System<MetaDataSystem>().SetEntityName(paper, "Содержимое контейнера");
            var containers = entities.System<Robust.Shared.Containers.SharedContainerSystem>();
            Assert.That(containers.Insert(paper, containers.EnsureContainer<Robust.Shared.Containers.Container>(chair, "keepsake")), Is.True);
            var removed = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(1.5f, .5f)));
            entities.System<MetaDataSystem>().SetEntityName(removed, "Удалённый в черновике");
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var editor = entities.System<ServerEditor>();
            Assert.That(editor.Open(player, grid), Does.Contain("открыт"));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var draft = DraftFor(editor, player);
            var token = (Guid) draft.GetType().GetField("Token")!.GetValue(draft)!;
            var items = (List<GridEditorItem>) draft.GetType().GetField("Items")!.GetValue(draft)!;
            var id = items.Single(item => item.Kind == "entity" && item.X == .5f).Id;
            var bytes = new byte[16 * 16 * 7];
            // Plan tile ID 1 deliberately differs from the native serializer's allocation.
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2 * 7, 4), 1);
            var plan = $$"""
                gridEditorIds:
                  2: '{{id}}'
                  3: new_chair
                tilemap: {0: Space, 1: Plating}
                entities:
                - proto: ''
                  entities:
                  - uid: 1
                    components:
                    - type: MapGrid
                      chunks:
                        0,0: {ind: '0,0', tiles: '{{Convert.ToBase64String(bytes)}}', version: 7}
                    - type: DecalGrid
                      chunkCollection: {version: 2, nodes: []}
                - proto: ChairWood
                  entities:
                  - uid: 2
                    components:
                    - {type: Transform, parent: 1, pos: '2.5,0.5', rot: 90, anchored: true}
                  - uid: 3
                    components:
                    - {type: Transform, parent: 1, pos: '3.5,0.5', rot: 0, anchored: false}
                """;
            frozenPlan = plan + "\n# " + new string('Ж', 40000) + "\n";
            Assert.Throws<System.IO.InvalidDataException>(() => editor.ExportDraft(player, Guid.NewGuid(), plan));
            var yaml = editor.ExportDraft(player, token, plan);
            Assert.That(yaml, Does.Not.Contain("gridEditorIds"));
            var loadOptions = new Robust.Shared.EntitySerialization.MapLoadOptions
            {
                DeserializationOptions = new Robust.Shared.EntitySerialization.DeserializationOptions { LogOrphanedGrids = false }
            };
            using (var reader = new System.IO.StringReader(yaml))
            {
                Assert.That(entities.System<MapLoaderSystem>().TryLoadGeneric(reader, "preserved-draft", out var loaded, loadOptions), Is.True);
                var loadedGrid = loaded!.Grids.Single();
                Assert.That(entities.GetComponent<MetaDataComponent>(loadedGrid).EntityName, Is.EqualTo("Грид с настройками"));
                Assert.That(map.GetTileRef(loadedGrid, loadedGrid.Comp, new Vector2i(0, 0)).Tile.IsEmpty, Is.True);
                Assert.That(map.GetTileRef(loadedGrid, loadedGrid.Comp, new Vector2i(2, 0)).Tile.TypeId, Is.EqualTo(floor.TypeId));
                var loadedChair = loaded.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == "Особый стул");
                var loadedSettings = entities.GetComponent<Content.Shared.Lock.LockComponent>(loadedChair);
                Assert.That(loadedSettings.Locked, Is.False); Assert.That(loadedSettings.LockOnClick, Is.True);
                Assert.That(loadedSettings.UnlockOnClick, Is.False); Assert.That(loadedSettings.BreakOnAccessBreaker, Is.False);
                Assert.That(entities.GetComponent<TransformComponent>(loadedChair).LocalPosition, Is.EqualTo(new Vector2(2.5f, .5f)));
                Assert.That(entities.GetComponent<TransformComponent>(loadedChair).Anchored, Is.True);
                var loadedPaper = loaded.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == "Содержимое контейнера");
                Assert.That(containers.GetContainer(loadedChair, "keepsake").ContainedEntities, Does.Contain(loadedPaper));
                Assert.That(loaded.Entities.Any(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == "Удалённый в черновике"), Is.False);
                Assert.That(loaded.Entities.Count(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "ChairWood"), Is.EqualTo(2));
            }
            Assert.That(entities.GetComponent<TransformComponent>(chair).LocalPosition, Is.EqualTo(new Vector2(.5f, .5f)));
            Assert.That(entities.EntityExists(removed), Is.True);
            Assert.That(map.GetTileRef(grid, grid.Comp, new Vector2i(0, 0)).Tile.IsEmpty, Is.False);
            Assert.That(map.GetTileRef(grid, grid.Comp, new Vector2i(2, 0)).Tile.IsEmpty, Is.True);
            foreach (var wholeMap in new[] { false, true })
            {
                draft.GetType().GetField("LastDownload")!.SetValue(draft, TimeSpan.Zero);
                var native = editor.Export(player, token, wholeMap, out var error);
                Assert.That(native, Is.Not.Null, error);
                using var reader = new System.IO.StringReader(native!);
                Assert.That(entities.System<MapLoaderSystem>().TryLoadGeneric(reader, "preserved-native", out var loaded, loadOptions), Is.True);
                var saved = loaded!.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == "Особый стул");
                Assert.That(entities.GetComponent<Content.Shared.Lock.LockComponent>(saved).UnlockOnClick, Is.False);
                Assert.That(containers.GetContainer(saved, "keepsake").ContainedEntities.Count, Is.EqualTo(1));
                var savedStock = entities.GetComponent<Content.Shared.VendingMachines.VendingMachineComponent>(
                    loaded.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "VendingMachineTankDispenserEVAPOI"));
                Assert.That(savedStock.Inventory["Paper"].Amount, Is.EqualTo(3));
                Assert.That(savedStock.EmaggedInventory["Paper"].Type, Is.EqualTo(Content.Shared.VendingMachines.InventoryType.Emagged));
                Assert.That(savedStock.EmaggedInventory["Paper"].Amount, Is.EqualTo(2));
                Assert.That(savedStock.ContrabandInventory["Paper"].Amount, Is.Zero);
            }
            draft.GetType().GetField("LastDownload")!.SetValue(draft, TimeSpan.Zero);
        });
        await pair.RunTicksSync(20);
        using var savedFile = new System.IO.MemoryStream();
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var dialogs = new Moq.Mock<Robust.Client.UserInterface.IFileDialogManager>();
            dialogs.Setup(dialog => dialog.SaveFile(Moq.It.IsAny<Robust.Client.UserInterface.FileDialogFilters?>(), true,
                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                .Returns(Task.FromResult<(System.IO.Stream, bool)?>((savedFile, false)));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ClientEditor).GetField("_dialogs", flags)!.SetValue(editor, dialogs.Object);
            var action = typeof(ClientEditor).GetMethod("Action", flags)!;
            void Send(string name, object payload) => action.Invoke(editor, new[] { name, JsonSerializer.Serialize(payload) });
            var chunks = (frozenPlan.Length + 1023) / 1024;
            Send("draftFileStart", new { id = "native-transfer", length = frozenPlan.Length, chunks });
            for (var index = 0; index < chunks; index++) Send("draftFileChunk", new { id = "native-transfer", index,
                text = frozenPlan.Substring(index * 1024, Math.Min(1024, frozenPlan.Length - index * 1024)) });
        });
        await pair.RunTicksSync(90);
        await pair.Client.WaitAssertion(() =>
        {
            var downloaded = System.Text.Encoding.UTF8.GetString(savedFile.ToArray());
            Assert.That(downloaded, Does.Contain("Особый стул"));
            using var reader = new System.IO.StringReader(downloaded);
            var document = (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;
            var savedLock = document.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()
                .SelectMany(group => group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>())
                .SelectMany(entity => entity.TryGet<SequenceDataNode>("components", out var components)
                    ? components.Cast<MappingDataNode>() : Enumerable.Empty<MappingDataNode>())
                .Single(component => component.Get<ValueDataNode>("type").Value == "Lock");
            Assert.That(bool.Parse(savedLock.Get<ValueDataNode>("unlockOnClick").Value), Is.False);
            Assert.That(downloaded, Does.Not.Contain("gridEditorIds"));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.That(new TabField("DownloadBusy").GetValue(pair.Client.EntMan.System<ClientEditor>()), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HotbarPersistsExplicitSlotsAndClearingAcrossLibraryReload()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var map = pair.Server.EntMan.System<SharedMapSystem>();
            map.CreateMap(out var mapId, runMapInit: false);
            var grid = map.CreateGridEntity(mapId);
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            Assert.That(pair.Server.EntMan.System<ServerEditor>().Open(player, grid), Does.Contain("открыт"));
        });
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var action = typeof(ClientEditor).GetMethod("Action", flags)!;
            var resources = pair.Client.ResolveDependency<IResourceManager>().UserData;
            resources.CreateDir(new ResPath("/grid-editor"));
            using (var legacy = resources.OpenWriteText(new ResPath("/grid-editor/library.json")))
                legacy.Write("""{"version":1,"favorites":[{"kind":"entity","id":"WallSolid"}],"hotbar":[{"slot":0,"kind":"entity","id":"WallSolid"}],"templates":[]}""");
            typeof(ClientEditor).GetField("_libraryLoaded", flags)!.SetValue(editor, false);
            typeof(ClientEditor).GetMethod("LoadLibrary", flags)!.Invoke(editor, null);
            Assert.That(((IDictionary) typeof(ClientEditor).GetField("_hotbar", flags)!.GetValue(editor)!).Count, Is.Zero,
                "Legacy favorites and automatically seeded slots must not populate a new hotbar");
            void Slot(int slot, string? id = null) => action.Invoke(editor, new object[] { "hotbarSet", JsonSerializer.Serialize(new { slot, kind = "entity", id = id ?? "" }) });
            Slot(0, "ChairWood"); Slot(25, "WallSolid"); Slot(0);
            action.Invoke(editor, new object[] { "favorite", """{"kind":"tile","id":"Plating"}""" });
            typeof(ClientEditor).GetField("_libraryLoaded", flags)!.SetValue(editor, false);
            typeof(ClientEditor).GetMethod("LoadLibrary", flags)!.Invoke(editor, null);
            var hotbar = (IDictionary) typeof(ClientEditor).GetField("_hotbar", flags)!.GetValue(editor)!;
            Assert.That(hotbar.Contains(0), Is.False);
            Assert.That(hotbar.Contains(25), Is.True);
            using var file = pair.Client.ResolveDependency<IResourceManager>().UserData.OpenText(new ResPath("/grid-editor/library.json"));
            var json = JsonDocument.Parse(file.ReadToEnd());
            Assert.That(json.RootElement.GetProperty("hotbar").EnumerateArray().Single().GetProperty("slot").GetInt32(), Is.EqualTo(25));
            // Native events queued during one tick need not arrive in insertion order.
            using var saved = new System.IO.MemoryStream();
            var dialogs = new Moq.Mock<Robust.Client.UserInterface.IFileDialogManager>();
            dialogs.Setup(dialog => dialog.SaveFile(Moq.It.IsAny<Robust.Client.UserInterface.FileDialogFilters?>(), true,
                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                .Returns(Task.FromResult<(System.IO.Stream, bool)?>((saved, false)));
            typeof(ClientEditor).GetField("_dialogs", flags)!.SetValue(editor, dialogs.Object);
            var request = Guid.NewGuid();
            new TabField("DownloadRequest").SetValue(editor, request);
            new TabField("Download").SetValue(editor, new System.IO.MemoryStream());
            new TabField("DownloadIndex").SetValue(editor, 0);
            new TabField("DownloadCount").SetValue(editor, 0);
            var bytes = Enumerable.Range(0, 65543).Select(index => (byte) (index % 251)).ToArray();
            var receive = typeof(ClientEditor).GetMethod("ReceiveFile", flags)!;
            var last = new GridEditorFileEvent(request, 1, 2, bytes.Skip(65536).ToArray());
            receive.Invoke(editor, new object[] { last });
            receive.Invoke(editor, new object[] { last });
            Assert.That(saved.ToArray(), Is.Empty);
            receive.Invoke(editor, new object[] { new GridEditorFileEvent(request, 0, 2, bytes.Take(65536).ToArray()) });
            Assert.That(saved.ToArray(), Is.EqualTo(bytes));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GridNameRequiresCurrentMappingSessionAndPersistsThroughMapExport()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Guid token = default;
        const string original = "Исходный grid";
        const string renamed = "Жилой модуль «Маяк»: палуба 2 #1";
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId, runMapInit: false);
            var created = map.CreateGridEntity(mapId); grid = created.Owner;
            map.SetTile(created, created.Comp, new Vector2i(0, 0), new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
            entities.System<MetaDataSystem>().SetEntityName(grid, original);
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            Assert.That(entities.System<ServerEditor>().Open(player, grid), Does.Contain("открыт"));
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>(); token = editor.State!.Token;
            Assert.That(((GridEditorEnvironmentState) new TabField("EnvironmentState").GetValue(editor)!).GridName, Is.EqualTo(original));
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var editor = entities.System<ServerEditor>();
            var player = pair.Server.PlayerMan.Sessions.Single();
            void Rename(Guid requestToken, string name) => typeof(ServerEditor).GetMethod("RenameGrid", flags)!.Invoke(editor,
                new object[] { new GridEditorNameRequest(requestToken, name), new EntitySessionEventArgs(player) });
            Rename(Guid.NewGuid(), "Stale request");
            foreach (var invalid in new[] { "", "   ", "a\nb", new string('x', GridEditorNameRequest.MaxLength + 1) }) Rename(token, invalid);
            Assert.That(entities.GetComponent<MetaDataComponent>(grid).EntityName, Is.EqualTo(original));
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() => typeof(ClientEditor).GetMethod("Action", flags)!.Invoke(pair.Client.EntMan.System<ClientEditor>(),
            new object[] { "setGridName", JsonSerializer.Serialize(new { name = "  " + renamed + "  " }) }));
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() => Assert.That(((GridEditorEnvironmentState) new TabField("EnvironmentState")
            .GetValue(pair.Client.EntMan.System<ClientEditor>())!).GridName, Is.EqualTo(renamed)));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<MetaDataComponent>(grid).EntityName, Is.EqualTo(renamed));
            var player = pair.Server.PlayerMan.Sessions.Single();
            var editor = entities.System<ServerEditor>();
            var yaml = editor.Export(player, token, false, out var error);
            Assert.That(yaml, Is.Not.Null, error);
            using var reader = new System.IO.StringReader(yaml!);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGeneric(reader, "renamed-grid", out var loaded), Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(loaded!.Grids.Single().Owner).EntityName, Is.EqualTo(renamed));
            pair.Server.ResolveDependency<IAdminManager>().DeAdmin(player);
            typeof(ServerEditor).GetMethod("RenameGrid", flags)!.Invoke(editor,
                new object[] { new GridEditorNameRequest(token, "No permission"), new EntitySessionEventArgs(player) });
            Assert.That(entities.GetComponent<MetaDataComponent>(grid).EntityName, Is.EqualTo(renamed));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ComplexMoveCreatesDecalStorageAndRestoresDeletedDraftEntity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId, runMapInit: false);
            var grid = map.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            map.SetTiles(grid, grid.Comp, new() { (new(0, 0), floor), (new(1, 0), floor) });
            var chair = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            var editor = entities.System<ServerEditor>();
            Assert.That(editor.Open(player, grid), Does.Contain("открыт"));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var drafts = (IDictionary) typeof(ServerEditor).GetField("_drafts", flags)!.GetValue(editor)!;
            var draft = DraftFor(editor, player);
            var token = (Guid) draft.GetType().GetField("Token")!.GetValue(draft)!;
            List<GridEditorItem> Items() => (List<GridEditorItem>) draft.GetType().GetField("Items")!.GetValue(draft)!;
            var chairId = Items().Single(item => item.Kind == "entity").Id;
            var decal = pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<DecalPrototype>().First().ID;
            void Apply(List<GridEditorItem> items) => typeof(ServerEditor).GetMethod("Apply", flags)!.Invoke(editor,
                new object[] { new GridEditorApplyEvent(token, items, patch: true), new EntitySessionEventArgs(player) });
            Apply(new() { new() { Id = "new_decal", Kind = "decal", Prototype = decal, X = .125f, Y = .75f } });
            Assert.That(entities.TryGetComponent<DecalGridComponent>(grid, out var decals), Is.True);
            var decalEntry = decals!.ChunkCollection.ChunkCollection.Values.SelectMany(chunk => chunk.Decals).Single();
            entities.DeleteEntity(chair);
            Apply(new() {
                new() { Id = "t:0:0", Kind = "remove" },
                new() { Id = "t:2:0", Kind = "tile", Prototype = "Plating", X = 2, Y = 0 },
                new() { Id = chairId, Kind = "entity", Prototype = "ChairWood", X = 2.5f, Y = .5f, Anchored = true, Rotation = 90 },
                new() { Id = "d:" + decalEntry.Key, Kind = "decal", Prototype = decal, X = 2.125f, Y = .75f }
            });
            Assert.That(map.GetTileRef(grid, grid.Comp, new Vector2i(0, 0)).Tile.IsEmpty, Is.True);
            Assert.That(map.GetTileRef(grid, grid.Comp, new Vector2i(2, 0)).Tile.IsEmpty, Is.False);
            Assert.That(decals.ChunkCollection.ChunkCollection.Values.SelectMany(chunk => chunk.Decals).Single().Value.Coordinates,
                Is.EqualTo(new Vector2(2.125f, .75f)));
            var restored = Items().Single(item => item.Prototype == "ChairWood");
            Assert.That(restored.X, Is.EqualTo(2.5f));
            Assert.That(restored.Anchored, Is.True);
            Assert.That(restored.Rotation, Is.EqualTo(90f).Within(.01f));
            Assert.That(grid.Comp.CanSplit, Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GridSourceShowsExactRepositoryFileAndSurvivesWorldMovesAndLivePatches()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TryLoadGenericTracked(new ResPath("/Maps/Test/empty.yml"), out var loaded), Is.True);
            grid = loaded!.Grids.Single().Owner;
            var editor = entities.System<ServerEditor>();
            Assert.That(editor.SourcePath(grid), Is.EqualTo("Resources/Maps/Test/empty.yml"));
            var map = entities.System<SharedMapSystem>();var target=map.CreateMap(out var mapId, runMapInit:false);
            entities.System<SharedTransformSystem>().SetCoordinates(grid,new EntityCoordinates(target,new Vector2(20,20)));
            Assert.That(editor.SourcePath(grid), Is.EqualTo("Resources/Maps/Test/empty.yml"));
            var manual=map.CreateGridEntity(mapId);
            Assert.That(editor.SourcePath(manual), Is.Empty, "Sharing a world map must not invent file provenance");
            Assert.That(loader.TryLoadGenericTracked(new ResPath("/Maps/Test/floor3x3.yml"), out var second), Is.True);
            Assert.That(editor.SourcePath(second!.Grids.Single().Owner), Is.EqualTo("Resources/Maps/Test/floor3x3.yml"));
            Assert.That(editor.SourcePath(grid), Is.EqualTo("Resources/Maps/Test/empty.yml"));
            using var text=new System.IO.StringWriter();Assert.That(loader.TrySaveGrid(grid,text),Is.True);
            Assert.That(text.ToString(), Does.Not.Contain("GridMapSource"), "Provenance must never be embedded in exported map YAML");
            using var reader=new System.IO.StringReader(text.ToString());
            Assert.That(loader.TryLoadGeneric(reader,"uploaded-map",out var uploaded),Is.True);
            Assert.That(editor.SourcePath(uploaded!.Grids.Single().Owner),Is.Empty);
            var player=pair.Server.PlayerMan.Sessions.Single();pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            Assert.That(editor.Open(player,grid),Does.Contain("открыт"));
        });
        await pair.RunTicksSync(25);
        await pair.Client.WaitAssertion(()=>Assert.That(pair.Client.EntMan.System<ClientEditor>().State!.SourcePath,Is.EqualTo("Resources/Maps/Test/empty.yml")));
        await pair.Server.WaitAssertion(()=>{
            var entities=pair.Server.EntMan;var map=entities.System<SharedMapSystem>();
            map.SetTile(grid,entities.GetComponent<MapGridComponent>(grid),new Vector2i(0,0),new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorSteel"].TileId));
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(()=>Assert.That(pair.Client.EntMan.System<ClientEditor>().State!.SourcePath,Is.EqualTo("Resources/Maps/Test/empty.yml")));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MapperSpriteOriginAndPipeRoutingPortsMatchNativePrototypes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var images = new Content.Client._DeepLagoon.WebUI.TguiSpriteImages();
            var json = new Content.Client._DeepLagoon.WebUI.TguiData().Array("images", images.MapItem("AlwaysPoweredWallLight")).ToString();
            using var parsed = JsonDocument.Parse(json);
            var layers = parsed.RootElement.GetProperty("images");
            Assert.That(layers.GetArrayLength(), Is.GreaterThan(0));
            foreach (var layer in layers.EnumerateArray())
            {
                Assert.That(layer.GetProperty("native").GetBoolean(), Is.True);
                Assert.That(layer.GetProperty("m32").GetSingle(), Is.EqualTo(1f));
                Assert.That(layer.GetProperty("width").GetInt32(), Is.GreaterThan(0));
                Assert.That(layer.GetProperty("url").GetString(), Does.StartWith("data:image/png;base64,"));
            }
        });
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            foreach (var suffix in new[] { "", "Alt1", "Alt2" })
            foreach (var (shape, expected) in new[] { ("Half", PipeDirection.South), ("Straight", PipeDirection.Longitudinal),
                         ("Bend", PipeDirection.SWBend), ("TJunction", PipeDirection.TSouth), ("Fourway", PipeDirection.Fourway) })
            {
                var prototype = prototypes.Index<EntityPrototype>($"GasPipe{shape}{suffix}");
                var nodes = (Content.Shared.NodeContainer.NodeContainerComponent) prototype.Components["NodeContainer"].Component;
                var pipe = (Content.Server.NodeContainer.Nodes.PipeNode) nodes.Nodes["pipe"];
                Assert.That(pipe.OriginalPipeDirection, Is.EqualTo(expected));
            }
            Assert.That(PipeDirection.SWBend.RotatePipeDirection(Angle.FromDegrees(270)), Is.EqualTo(PipeDirection.NWBend));
            entitiesForDisposal();
            void entitiesForDisposal()
            {
                var entities=pair.Server.EntMan;var map=entities.System<SharedMapSystem>();map.CreateMap(out var mapId,runMapInit:false);
                var grid=map.CreateGridEntity(mapId);
                var floor=new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
                map.SetTile(grid,grid.Comp,new Vector2i(0,0),floor);
                foreach(var (prototype,expected) in new[]{("DisposalPipe",PipeDirection.Longitudinal),("DisposalBend",PipeDirection.SWBend),
                    ("DisposalJunction",PipeDirection.TWest),("DisposalJunctionFlipped",PipeDirection.TEast),("DisposalYJunction",PipeDirection.TSouth)})
                {
                    var pipe=entities.SpawnEntity(prototype,new EntityCoordinates(grid.Owner,new Vector2(.5f,.5f)));
                    var ports=new Content.Server.Disposal.Tube.GetDisposalsConnectableDirectionsEvent();entities.EventBus.RaiseLocalEvent(pipe,ref ports);
                    var mask=ports.Connectable.Aggregate(PipeDirection.None,(current,direction)=>current|direction.ToPipeDirection());
                    Assert.That(mask,Is.EqualTo(expected),prototype);entities.DeleteEntity(pipe);
                }
            }
            foreach (var cable in new[] { "CableHV", "CableMV", "CableApcExtension" }) Assert.That(prototypes.HasIndex<EntityPrototype>(cable), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LiveGridAndForeignDraftsSyncAcrossEditorsWhileLocalApplyAlwaysWins()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var dummy = await pair.Server.AddDummySession("second_mapper");
        await PoolManager.WaitUntil(pair.Server, () => dummy.Status == SessionStatus.InGame, 600);
        EntityUid grid = default, chair = default;
        ICommonSession first = null!;
        ServerEditor server = null!;
        object Draft(ICommonSession player) => DraftFor(server, player);
        Guid Token(ICommonSession player) => (Guid) Draft(player).GetType().GetField("Token")!.GetValue(Draft(player))!;
        List<GridEditorItem> Items(ICommonSession player) => (List<GridEditorItem>) Draft(player).GetType().GetField("Items")!.GetValue(Draft(player))!;
        Dictionary<string, GridEditorItem> PreviewItems(ICommonSession player) => (Dictionary<string, GridEditorItem>) Draft(player).GetType().GetField("Preview")!.GetValue(Draft(player))!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Preview(ICommonSession player, int generation, GridEditorItem item) => typeof(ServerEditor).GetMethod("Preview", flags)!
            .Invoke(server, new object[] { new GridEditorDraftUpdateEvent(Token(player), generation, new() { item }, new GridEditorExpansion { Left = 6 }), new EntitySessionEventArgs(player) });
        var action = typeof(ClientEditor).GetMethod("Action", flags)!;
        void SendClient(string purpose, GridEditorItem item)
        {
            var client = pair.Client.EntMan.System<ClientEditor>(); var id = Guid.NewGuid().ToString();
            action.Invoke(client, new object[] { purpose + "Start", JsonSerializer.Serialize(new { id, count = 1, chunks = 1, generation = 0 }) });
            action.Invoke(client, new object[] { purpose + "Chunk", JsonSerializer.Serialize(new { id, index = 0, items = new[] {
                new { id = item.Id, kind = item.Kind, prototype = item.Prototype, x = item.X, y = item.Y,
                    rotation = item.Rotation, anchored = item.Anchored, color = item.Color } } }) });
        }
        try
        {
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                first = pair.Server.PlayerMan.Sessions.Single(player => player != dummy);
                var admins = pair.Server.ResolveDependency<IAdminManager>(); admins.PromoteHost(first); admins.PromoteHost(dummy);
                var map = entities.System<SharedMapSystem>(); map.CreateMap(out var mapId, runMapInit: false);
                var created = map.CreateGridEntity(mapId); grid = created.Owner;
                var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
                map.SetTiles(grid, created.Comp, new() { (new(0, 0), floor), (new(1, 0), floor), (new(2, 0), floor) });
                chair = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
                server = entities.System<ServerEditor>();
                Assert.That(server.Open(first, grid), Does.Contain("открыт"));
                Assert.That(server.Open(dummy, grid), Does.Contain("открыт"));
            });
            await pair.RunTicksSync(30);
            Guid firstToken = default; string chairId = null!;
            await pair.Client.WaitAssertion(() => { firstToken = pair.Client.EntMan.System<ClientEditor>().State!.Token;
                chairId = pair.Client.EntMan.System<ClientEditor>().State!.Items.Single(item => item.Prototype == "ChairWood").Id; });
            await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(chair,
                new EntityCoordinates(grid, new Vector2(1.5f, .5f))));
            await pair.RunTicksSync(30);
            await pair.Client.WaitAssertion(() => Assert.That(pair.Client.EntMan.System<ClientEditor>().State!.Items.Single(item => item.Id == chairId).X, Is.EqualTo(1.5f)));
            var edited = new GridEditorItem { Id = chairId, Kind = "entity", Prototype = "ChairWood", X = .5f, Y = .5f, Rotation = 90 };
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(Items(dummy).Single(item => item.Id == chairId).X, Is.EqualTo(1.5f));
                Preview(dummy, 0, edited);
                Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(chair).LocalPosition.X, Is.EqualTo(1.5f), "A preview must not write to the world");
            });
            await pair.RunTicksSync(15);
            await pair.Client.WaitAssertion(() =>
            {
                var peers = (List<GridEditorPeerDraft>) new TabField("PeerDrafts").GetValue(pair.Client.EntMan.System<ClientEditor>())!;
                Assert.That(peers.Single(peer => peer.Name == dummy.Name).Items.Single().X, Is.EqualTo(.5f));
                Assert.That(peers.Single(peer => peer.Name == dummy.Name).Expansion.Left, Is.EqualTo(6));
                SendClient("preview", new GridEditorItem { Id = "t:2:0", Kind = "tile", Prototype = "FloorSteel", X = 2, Y = 0 });
            });
            await pair.RunTicksSync(15);
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(PreviewItems(first)["t:2:0"].Prototype, Is.EqualTo("FloorSteel"));
                // Either mapper can take over, rotate and delete a peer's unsaved creation.
                var creation = new GridEditorItem { Id = "new_shared_chair", Kind = "entity", Prototype = "ChairWood", X = 1.5f, Y = .5f };
                Preview(dummy, 0, creation);
                Preview(first, 0, new GridEditorItem { Id = creation.Id, Kind = "entity", Prototype = "ChairWood", X = 2.5f, Y = .5f, Rotation = 90 });
                Assert.That(PreviewItems(dummy).ContainsKey(creation.Id), Is.False);
                Assert.That(PreviewItems(first)[creation.Id].Rotation, Is.EqualTo(90));
                var generation = (int) Draft(dummy).GetType().GetField("PreviewGeneration")!.GetValue(Draft(dummy))!;
                Preview(dummy, generation, new GridEditorItem { Id = creation.Id, Kind = "remove" });
                Assert.That(PreviewItems(first).ContainsKey(creation.Id), Is.False);
                Assert.That(PreviewItems(dummy)[creation.Id].Kind, Is.EqualTo("remove"));
                // Restore the original pending chair intent for the following apply assertions.
                Preview(dummy, generation, edited);
                typeof(ServerEditor).GetMethod("Apply", flags)!.Invoke(server, new object[] {
                    new GridEditorApplyEvent(Token(dummy), new() { edited }, patch: true), new EntitySessionEventArgs(dummy) });
                Assert.That(PreviewItems(dummy), Is.Empty);
                Assert.That(PreviewItems(first).ContainsKey("t:2:0"), Is.True, "Another editor's apply must not clear this user's draft");
                Preview(dummy, 0, new GridEditorItem { Id = "new_late", Kind = "entity", Prototype = "ChairWood", X = 2.5f, Y = .5f });
                Assert.That(PreviewItems(dummy), Is.Empty, "An obsolete in-flight preview must not reappear after Apply");
            });
            await pair.RunTicksSync(15);
            await pair.Client.WaitAssertion(() =>
            {
                var state = pair.Client.EntMan.System<ClientEditor>().State!;
                Assert.That(state.Token, Is.EqualTo(firstToken));
                Assert.That(state.Items.Single(item => item.Id == chairId).X, Is.EqualTo(.5f));
            });
            await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(chair,
                new EntityCoordinates(grid, new Vector2(2.5f, .5f))));
            edited.Rotation = 180;
            await pair.Client.WaitAssertion(() => SendClient("apply", edited));
            await pair.RunTicksSync(20);
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(chair).LocalPosition.X, Is.EqualTo(.5f));
                Assert.That(Items(dummy).Single(item => item.Id == chairId).X, Is.EqualTo(.5f));
                pair.Server.EntMan.DeleteEntity(chair);
            });
            edited.X = 1.5f;
            await pair.Client.WaitAssertion(() => SendClient("apply", edited));
            await pair.RunTicksSync(20);
            await pair.Client.WaitAssertion(() =>
            {
                var recreated = pair.Client.EntMan.System<ClientEditor>().State!.Items.Single(item => item.Prototype == "ChairWood");
                Assert.That(recreated.X, Is.EqualTo(1.5f)); Assert.That(recreated.Id, Is.Not.EqualTo(chairId));
                action.Invoke(pair.Client.EntMan.System<ClientEditor>(), new object[] { "close", "{}" });
            });
            await pair.RunTicksSync(10);
        }
        finally { await pair.Server.RemoveDummySession(dummy); }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AtmosDiagnosticsFindOpenBoundariesAndOnlyFillSealedRooms()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId, runMapInit: false);
            var grid = map.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 7; x++) for (var y = 0; y < 3; y++) tiles.Add((new(x, y), floor));
            map.SetTiles(grid.Owner, grid.Comp, tiles);
            for (var x = 0; x < 7; x++) for (var y = 0; y < 3; y++)
                if ((x == 0 || x == 3 || x == 6 || y == 0 || y == 2) && (x != 6 || y != 1))
                    entities.SpawnEntity("WallSolid", new EntityCoordinates(grid.Owner, new Vector2(x + .5f, y + .5f)));
            var atmos = entities.System<AtmosphereSystem>();
            var hadAtmosphere = atmos.HasAtmosphere(grid.Owner);
            var mapUid = entities.GetComponent<TransformComponent>(grid.Owner).MapUid;
            var originalPressure = atmos.GetTileMixture(grid.Owner, mapUid, new Vector2i(1, 1))?.Pressure;
            var analysis = atmos.InspectGridEditorAtmos(grid.Owner, Guid.Empty);
            Assert.That(atmos.HasAtmosphere(grid.Owner), Is.EqualTo(hadAtmosphere), "Diagnostics must not add or remove atmosphere support");
            Assert.That(atmos.GetTileMixture(grid.Owner, mapUid, new Vector2i(1, 1))?.Pressure, Is.EqualTo(originalPressure),
                "Diagnostics must not alter gas mixtures");
            Assert.That(analysis.Cells.Count(c => c.Status == GridEditorAirStatus.Sealed), Is.EqualTo(2));
            Assert.That(analysis.Cells.Single(c => c.X == 6 && c.Y == 1).Leaks, Is.EqualTo(AtmosDirection.East));
            var filled = atmos.InspectGridEditorAtmos(grid.Owner, Guid.Empty, fill: true);
            Assert.That(filled.Filled, Is.EqualTo(2));
            Assert.That(filled.Cells.Single(c => c.X == 1 && c.Y == 1).Pressure, Is.EqualTo(101.325f).Within(.2f));
            Assert.That(filled.Cells.Single(c => c.X == 5 && c.Y == 1).Pressure, Is.EqualTo(0));
            var closure = entities.SpawnEntity("WallSolid", new EntityCoordinates(grid.Owner, new Vector2(6.5f, 1.5f)));
            Assert.That(atmos.InspectGridEditorAtmos(grid.Owner, Guid.Empty, fill: true).Filled, Is.EqualTo(4));
            // A blocker changing state, like a door opening, must immediately change the diagnosis.
            entities.System<AirtightSystem>().SetAirblocked((closure, entities.GetComponent<AirtightComponent>(closure)), false);
            var reopened = atmos.InspectGridEditorAtmos(grid.Owner, Guid.Empty, fill: true);
            Assert.That(reopened.Filled, Is.EqualTo(2));
            Assert.That(reopened.Cells.Single(c => c.X == 5 && c.Y == 1).Status, Is.EqualTo(GridEditorAirStatus.Leaking));
            Assert.That(reopened.Cells.Single(c => c.X == 5 && c.Y == 1).Pressure, Is.EqualTo(101.325f).Within(.2f),
                "Filling sealed rooms must not reset gas in unrelated open areas");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DiagonalFoundationRotatesAndIncomingConstructionReplacesOccupiedCell()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().CreateMap(out var mapId, runMapInit: false);
            var created = pair.Server.EntMan.System<SharedMapSystem>().CreateGridEntity(mapId); grid = created.Owner;
            entities.System<SharedMapSystem>().SetTile(grid, created.Comp, new Vector2i(0, -1), new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(pair.Server.PlayerMan.Sessions.Single());
        });
        await PoolManager.WaitUntil(pair.Server, () => Task.FromResult(pair.Server.ResolveDependency<IAdminManager>().HasAdminFlag(pair.Server.PlayerMan.Sessions.Single(), AdminFlags.Mapping)));
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<ServerEditor>().Open(pair.Server.PlayerMan.Sessions.Single(), grid), Does.Contain("открыт")));
        await pair.RunTicksSync(30);
        var action = typeof(ClientEditor).GetMethod("Action", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Apply(string prototype, float rotation, bool replace = false)
        {
            await pair.Client.WaitAssertion(() =>
            {
                var editor = pair.Client.EntMan.System<ClientEditor>();
                var existing = editor.State!.Items.FirstOrDefault(item => item.Kind == "entity");
                var id = Guid.NewGuid().ToString();
                action.Invoke(editor, new object[] { "applyStart", JsonSerializer.Serialize(new { id, count = 1, chunks = 1 }) });
                action.Invoke(editor, new object[] { "applyChunk", JsonSerializer.Serialize(new { id, index = 0,
                    items = new[] { new { id = replace || existing == null ? "new_" + id : existing.Id, kind = "entity", prototype,
                        x = .5f, y = .5f, rotation, anchored = prototype != "ClosetSteelBase", color = "#FFFFFFFF", z = 0,
                        cleanable = false, variant = 0, flags = 0, mirror = 0 } } }) });
            });
            await pair.RunTicksSync(30);
        }
        await Apply("WallPlastitaniumDiagonalOutpost", 0);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<SharedMapSystem>()
            .GetTileRef(grid, pair.Server.EntMan.GetComponent<MapGridComponent>(grid), new Vector2i(0, 0)).Tile.TypeId,
            Is.EqualTo(pair.Server.ResolveDependency<ITileDefinitionManager>()[GridEditorPlacement.DiagonalFloor].TileId)));
        await Apply("WallPlastitaniumDiagonalOutpost", 90);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<SharedMapSystem>()
            .GetTileRef(grid, pair.Server.EntMan.GetComponent<MapGridComponent>(grid), new Vector2i(0, 0)).Tile.RotationMirroring, Is.EqualTo(3)));
        // The screenshot's inner corner has floors in the adjoining cells: keep a full square support.
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>(); var id = Guid.NewGuid().ToString();
            action.Invoke(editor, new object[] { "applyStart", JsonSerializer.Serialize(new { id, count = 3, chunks = 1 }) });
            var positions = new[] { new Vector2i(-1, 0), new Vector2i(1, 0), new Vector2i(0, 1) };
            action.Invoke(editor, new object[] { "applyChunk", JsonSerializer.Serialize(new { id, index = 0,
                items = positions.Select(p => new { id = $"t:{p.X}:{p.Y}", kind = "tile", prototype = "Plating", x = p.X, y = p.Y,
                    rotation = 0, color = "#FFFFFFFF", z = 0, cleanable = false, anchored = false, variant = 0, flags = 0, mirror = 0 }) }) });
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<SharedMapSystem>()
            .GetTileRef(grid, pair.Server.EntMan.GetComponent<MapGridComponent>(grid), new Vector2i(0, 0)).Tile.TypeId,
            Is.EqualTo(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId)));
        foreach (var prototype in new[] { "WallSolid", "Window", "Table", "ClosetSteelBase" })
        {
            await Apply(prototype, 0, replace: true);
            await pair.Client.WaitAssertion(() =>
            {
                var state = pair.Client.EntMan.System<ClientEditor>().State!;
                Assert.That(state.Items.Where(item => item.Kind == "entity" && MathF.Floor(item.X) == 0 && MathF.Floor(item.Y) == 0)
                    .Select(item => item.Prototype), Is.EqualTo(new[] { prototype }));
            });
        }
        await pair.Client.WaitAssertion(() => action.Invoke(pair.Client.EntMan.System<ClientEditor>(), new object[] { "close", "{}" }));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullGridBeyondOldSizeAndItemLimitsIsVisibleAndEditableAtItsFarEdge()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default, farChair = default, nearChair = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().CreateMap(out var mapId, runMapInit: false);
            var created = pair.Server.EntMan.System<SharedMapSystem>().CreateGridEntity(mapId);
            grid = created.Owner;
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = -125; x < 125; x++) for (var y = -125; y < 125; y++) tiles.Add((new(x, y), tile));
            entities.System<SharedMapSystem>().SetTiles(grid, created.Comp, tiles);
            nearChair = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(-124.5f, -124.5f)));
            farChair = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(124.5f, 124.5f)));
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(pair.Server.PlayerMan.Sessions.Single());
        });
        await PoolManager.WaitUntil(pair.Server, () => Task.FromResult(pair.Server.ResolveDependency<IAdminManager>()
            .HasAdminFlag(pair.Server.PlayerMan.Sessions.Single(), AdminFlags.Mapping)));
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<ServerEditor>()
            .Open(pair.Server.PlayerMan.Sessions.Single(), grid), Does.Contain("открыт")));
        await pair.RunTicksSync(60);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var state = editor.State!;
            Assert.That(state.Width, Is.GreaterThanOrEqualTo(250));
            Assert.That(state.Height, Is.GreaterThanOrEqualTo(250));
            Assert.That(state.Items.Count(i => i.Kind == "tile"), Is.EqualTo(62500));
            Assert.That(state.Items.Any(i => i.Id == "t:124:124"), Is.True);
            Assert.That(state.Items.Any(i => i.Id == "t:-125:-125"), Is.True);
            var item = state.Items.Single(i => i.Kind == "entity" && i.X == 124.5f && i.Y == 124.5f);
            var action = typeof(ClientEditor).GetMethod("Action", BindingFlags.Instance | BindingFlags.NonPublic)!;
            action.Invoke(editor, new object[] { "applyStart", "{\"id\":\"far-edge\",\"count\":1,\"chunks\":1}" });
            action.Invoke(editor, new object[] { "applyChunk", JsonSerializer.Serialize(new { id = "far-edge", index = 0,
                items = new[] { new { id = item.Id, kind = item.Kind, prototype = item.Prototype, x = 123.5f, y = item.Y, rotation = 90, anchored = item.Anchored } } }) });
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(farChair).LocalPosition, Is.EqualTo(new Vector2(123.5f, 124.5f)));
            Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(nearChair).LocalPosition, Is.EqualTo(new Vector2(-124.5f, -124.5f)));
            Assert.That(pair.Server.EntMan.System<SharedMapSystem>().GetAllTiles(grid, pair.Server.EntMan.GetComponent<MapGridComponent>(grid)).Count(), Is.EqualTo(62500));
        });
        // More than the old apply limit, with an unrelated live entity move.
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>()
            .SetLocalRotation(nearChair, Angle.FromDegrees(180)));
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var action = typeof(ClientEditor).GetMethod("Action", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var tiles = editor.State!.Items.Where(i => i.Kind == "tile").Take(3000).ToArray();
            action.Invoke(editor, new object[] { "applyStart", JsonSerializer.Serialize(new { id = "large-patch", count = tiles.Length, chunks = (tiles.Length + 15) / 16 }) });
            for (var index = 0; index < (tiles.Length + 15) / 16; index++)
                action.Invoke(editor, new object[] { "applyChunk", JsonSerializer.Serialize(new { id = "large-patch", index,
                    items = tiles.Skip(index * 16).Take(16).Select(i => new { id = i.Id, kind = i.Kind, prototype = i.Prototype,
                        x = i.X, y = i.Y, mirror = 1 }) }) });
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.System<SharedMapSystem>().GetAllTiles(grid, entities.GetComponent<MapGridComponent>(grid))
                .Count(t => t.Tile.RotationMirroring == 1), Is.EqualTo(3000));
            Assert.That(entities.GetComponent<TransformComponent>(nearChair).LocalRotation.Degrees, Is.EqualTo(180).Within(.01));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExpansionAddsEmptySpaceWithoutTileLimitsAndKeepsSessionToken()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().CreateMap(out var mapId, runMapInit: false);
            var created = pair.Server.EntMan.System<SharedMapSystem>().CreateGridEntity(mapId);
            grid = created.Owner;
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            // A 250x250 footprint with internal empty space that expansion must preserve.
            var border = new List<(Vector2i, Tile)>();
            for (var i = 0; i < 250; i++)
            {
                border.Add((new(i, 0), tile)); border.Add((new(i, 249), tile));
                if (i is > 0 and < 249) { border.Add((new(0, i), tile)); border.Add((new(249, i), tile)); }
            }
            entities.System<SharedMapSystem>().SetTiles(grid, created.Comp, border);
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(pair.Server.PlayerMan.Sessions.Single());
        });
        await PoolManager.WaitUntil(pair.Server, () => Task.FromResult(pair.Server.ResolveDependency<IAdminManager>()
            .HasAdminFlag(pair.Server.PlayerMan.Sessions.Single(), AdminFlags.Mapping)));
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<ServerEditor>()
            .Open(pair.Server.PlayerMan.Sessions.Single(), grid), Does.Contain("открыт")));
        await pair.RunTicksSync(30);
        var action = typeof(ClientEditor).GetMethod("Action", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Send(int left, int right, int bottom, int top, string tile = "Plating")
        {
            await pair.Client.WaitAssertion(() =>
            {
                var editor = pair.Client.EntMan.System<ClientEditor>();
                var id = Guid.NewGuid().ToString();
                action.Invoke(editor, new object[] { "applyStart", JsonSerializer.Serialize(new
                    { id, count = 0, chunks = 1, expansion = new { left, right, bottom, top, tile } }) });
                action.Invoke(editor, new object[] { "applyChunk", JsonSerializer.Serialize(new { id, index = 0, items = Array.Empty<object>() }) });
            });
            await pair.RunTicksSync(30);
        }
        await pair.Client.WaitAssertion(() =>
        {
            var state = pair.Client.EntMan.System<ClientEditor>().State!;
            Assert.That(state.GridWidth, Is.EqualTo(250));
            Assert.That(state.GridHeight, Is.EqualTo(250));
            Assert.That(state.Width, Is.GreaterThanOrEqualTo(250));
            Assert.That(state.Height, Is.GreaterThanOrEqualTo(250));
            Assert.That(state.Items.Any(i => i.Id == "t:249:249"), Is.True);
        });
        await Send(1, 1, 1, 1);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var component = entities.GetComponent<MapGridComponent>(grid);
            var map = entities.System<SharedMapSystem>();
            Assert.That(map.GetAllTiles(grid, component).Count(), Is.EqualTo(996));
            Assert.That(map.GetTileRef(grid, component, new Vector2i(100, 100)).Tile.IsEmpty, Is.True);
            foreach (var corner in new[] { new Vector2i(-1, -1), new Vector2i(250, 250), new Vector2i(-1, 250), new Vector2i(250, -1) })
                Assert.That(map.GetTileRef(grid, component, corner).Tile.IsEmpty, Is.True);
            Assert.That(component.CanSplit, Is.True);
        });
        Guid token = default;
        await pair.Client.WaitAssertion(() => token = pair.Client.EntMan.System<ClientEditor>().State!.Token);
        await Send(100, 100, 100, 100); // Empty space has no former side/area limits.
        var expandedWidth = 0;
        var expandedHeight = 0;
        await pair.Client.WaitAssertion(() => {
            var state = pair.Client.EntMan.System<ClientEditor>().State!;
            Assert.That(state.Token, Is.EqualTo(token));
            Assert.That(state.Width, Is.GreaterThan(450));
            expandedWidth = state.Width;
            expandedHeight = state.Height;
        });
        await Send(-1, 0, 0, 0);
        await pair.Client.WaitAssertion(() =>
        {
            var state = pair.Client.EntMan.System<ClientEditor>().State!;
            Assert.That(state.Token, Is.EqualTo(token));
            Assert.That(state.Items.Count(i => i.Kind == "tile"), Is.EqualTo(996));
            Assert.That(state.Width, Is.EqualTo(expandedWidth - 1));
        });
        await Send(-100, -100, -100, -100);
        await pair.Client.WaitAssertion(() =>
        {
            var state = pair.Client.EntMan.System<ClientEditor>().State!;
            Assert.That(state.Width, Is.EqualTo(expandedWidth - 201));
            Assert.That(state.Height, Is.EqualTo(expandedHeight - 200));
            Assert.That(state.Token, Is.EqualTo(token));
        });
        await Send(0, 1, 0, 0, "Space");
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<SharedMapSystem>()
            .GetAllTiles(grid, pair.Server.EntMan.GetComponent<MapGridComponent>(grid)).Count(), Is.EqualTo(996)));
        // Unrelated live changes do not expire the editor session.
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().SetTile(grid, entities.GetComponent<MapGridComponent>(grid), new Vector2i(250, 249),
                new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
        });
        await Send(0, 1, 0, 0);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.System<SharedMapSystem>()
                .GetAllTiles(grid, pair.Server.EntMan.GetComponent<MapGridComponent>(grid)).Count(), Is.EqualTo(997));
            pair.Server.ResolveDependency<IAdminManager>().DeAdmin(pair.Server.PlayerMan.Sessions.Single());
        });
        await Send(0, 1, 0, 0);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<SharedMapSystem>()
            .GetAllTiles(grid, pair.Server.EntMan.GetComponent<MapGridComponent>(grid)).Count(), Is.EqualTo(997)));
        await pair.Client.WaitAssertion(() => action.Invoke(pair.Client.EntMan.System<ClientEditor>(), new object[] { "close", "{}" }));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DraftAppliesTilesEntitiesAndDecalsOverridesLiveEditsAndRejectsUnauthorizedEdits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default, chair = default;
        string decalId = null!;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().CreateMap(out var mapId, runMapInit: false);
            var created = pair.Server.EntMan.System<SharedMapSystem>().CreateGridEntity(mapId);
            grid = created.Owner;
            var tiles = pair.Server.ResolveDependency<ITileDefinitionManager>();
            entities.System<SharedMapSystem>().SetTile(grid, created.Comp, new Vector2i(0, 0), new Tile(tiles["Plating"].TileId));
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(session);
            chair = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            entities.System<MetaDataSystem>().SetEntityName(chair, "Preserved mapper name");
            decalId = pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<DecalPrototype>().First().ID;
        });
        await PoolManager.WaitUntil(pair.Server, () => Task.FromResult(pair.Server.ResolveDependency<IAdminManager>()
            .HasAdminFlag(pair.Server.PlayerMan.Sessions.Single(), AdminFlags.Mapping)));
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<ServerEditor>()
            .Open(pair.Server.PlayerMan.Sessions.Single(), grid), Does.Contain("открыт")));
        await pair.RunTicksSync(30);
        var action = typeof(ClientEditor).GetMethod("Action", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var options = new JsonSerializerOptions { IncludeFields = true };
        GridEditorItem Copy(GridEditorItem item) => JsonSerializer.Deserialize<GridEditorItem>(JsonSerializer.Serialize(item, options), options)!;
        string Payload(IEnumerable<GridEditorItem> items) => JsonSerializer.Serialize(new { items = items.Select(i => new
        { id = i.Id, kind = i.Kind, prototype = i.Prototype, x = i.X, y = i.Y, rotation = i.Rotation, color = i.Color,
          z = i.Z, cleanable = i.Cleanable, anchored = i.Anchored, variant = i.Variant, flags = i.Flags, mirror = i.Mirror }) });
        void Send(ClientEditor editor, IEnumerable<GridEditorItem> items, string purpose = "apply", string name = "")
        {
            var list = items.ToList();
            var transfer = Guid.NewGuid().ToString();
            var chunks = Math.Max(1, (list.Count + 15) / 16);
            action.Invoke(editor, new object[] { "applyStart", JsonSerializer.Serialize(new { id = transfer, count = list.Count, chunks, purpose, name }) });
            for (var index = 0; index < chunks; index++)
            {
                var batch = JsonDocument.Parse(Payload(list.Skip(index * 16).Take(16))).RootElement.GetProperty("items");
                var json = JsonSerializer.Serialize(new { id = transfer, index, items = batch });
                Assert.That(json.Length, Is.LessThan(8192), "Every chunk must fit the native bridge limit");
                action.Invoke(editor, new object[] { "applyChunk", json });
            }
        }
        Guid oldToken = default;
        Guid savedToken = default;
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            Assert.That(editor.State, Is.Not.Null);
            oldToken = editor.State!.Token;
            var existing = Copy(editor.State.Items.Single(i => i.Prototype == "ChairWood"));
            var draft = new List<GridEditorItem> { existing, new() { Id = "t:0:0", Kind = "remove" } };
            existing.X = 6.5f; existing.Y = 6.5f; existing.Rotation = 90;
            draft.RemoveAll(i => i.Id == "t:6:6");
            draft.Add(new GridEditorItem { Id = "t:6:6", Kind = "tile", Prototype = "Plating", X = 6, Y = 6 });
            draft.Add(new GridEditorItem { Id = "new_decal", Kind = "decal", Prototype = decalId, X = 6, Y = 6 });
            Send(editor, draft);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<TransformComponent>(chair).LocalPosition, Is.EqualTo(new Vector2(6.5f, 6.5f)));
            Assert.That(entities.GetComponent<MetaDataComponent>(chair).EntityName, Is.EqualTo("Preserved mapper name"));
            Assert.That(entities.System<SharedMapSystem>().GetTileRef(grid, entities.GetComponent<MapGridComponent>(grid), new Vector2i(6, 6)).Tile.IsEmpty, Is.False);
            Assert.That(entities.System<SharedMapSystem>().GetTileRef(grid, entities.GetComponent<MapGridComponent>(grid), new Vector2i(0, 0)).Tile.IsEmpty, Is.True);
            Assert.That(entities.System<DecalSystem>().GetDecalsInRange(grid, new Vector2(6, 6)), Is.Not.Empty);
        });
        await pair.Client.WaitAssertion(() => Assert.That(pair.Client.EntMan.System<ClientEditor>().State!.Token, Is.EqualTo(oldToken)));
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            savedToken = editor.State!.Token;
            action.Invoke(editor, new object[] { "favorite", "{\"kind\":\"entity\",\"id\":\"WallSolid\"}" });
            Send(editor, editor.State.Items, "template", "Тестовая каюта");
            var file = pair.Client.ResolveDependency<IResourceManager>().UserData;
            Assert.That(file.Exists(new ResPath("/grid-editor/library.json")), Is.True);
            using var reader = file.OpenText(new ResPath("/grid-editor/library.json"));
            var json = JsonDocument.Parse(reader.ReadToEnd());
            Assert.That(json.RootElement.GetProperty("favorites").EnumerateArray().Any(f => f.GetProperty("id").GetString() == "WallSolid"), Is.True);
            Assert.That(json.RootElement.GetProperty("templates").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "Тестовая каюта"), Is.True);
            // Read the saved file again through the same startup path used after restarting the client.
            typeof(ClientEditor).GetField("_libraryLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, false);
            typeof(ClientEditor).GetMethod("LoadLibrary", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
        });
        await pair.Server.WaitAssertion(() =>
        {
            var editor = pair.Server.EntMan.System<ServerEditor>();
            var yaml = editor.Export(pair.Server.PlayerMan.Sessions.Single(), savedToken, false, out var error);
            Assert.That(yaml, Is.Not.Null, error);
            Assert.That(yaml, Does.Contain("ChairWood"));
            Assert.That(yaml, Does.Contain("Preserved mapper name"));
        });
        // Editor-authored changes take priority over external changes to the same entity.
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(chair,
            new EntityCoordinates(grid, new Vector2(7.5f, 6.5f))));
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            var conflict = editor.State!.Items.Select(Copy).ToList();
            conflict.Single(i => i.Prototype == "ChairWood").X = 6.5f;
            conflict.Single(i => i.Prototype == "ChairWood").Rotation = 180;
            Send(editor, conflict);
        });
        await pair.RunTicksSync(25);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(chair).LocalPosition.X, Is.EqualTo(6.5f));
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().DeAdmin(session);
            Assert.That(pair.Server.EntMan.System<ServerEditor>().Open(session, grid), Does.Contain("Mapping"));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ClientEditor>();
            Send(editor, editor.State!.Items);
            action.Invoke(editor, new object[] { "close", "{}" });
        });
        await pair.RunTicksSync(25);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(chair).LocalPosition.X, Is.EqualTo(6.5f)));
        await pair.CleanReturnAsync();
    }
}

[RegisterComponent]
public sealed partial class GridEditorBrokenExportComponent : Component
{
    [Robust.Shared.Serialization.Manager.Attributes.DataField]
    public GridEditorBrokenExportValue Value = new();
}

public sealed class GridEditorBrokenExportValue;
