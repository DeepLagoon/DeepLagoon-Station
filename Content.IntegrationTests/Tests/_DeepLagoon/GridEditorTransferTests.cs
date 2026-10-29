using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Content.Server.Administration.Managers;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Binary.Components;
using Content.Server.Atmos.Piping.Trinary.Components;
using Content.Server.NodeContainer.Nodes;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.Piping.Unary.Components;
using Content.Shared.Disposal.Components;
using Content.Shared.Disposal.Unit;
using Content.Shared.NodeContainer;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Content.Client._DeepLagoon.WebUI;
using ClientEditor = Content.Client._DeepLagoon.Mapping.GridEditorSystem;
using Editor = Content.Server._DeepLagoon.Mapping.GridEditorSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GridEditorTransferTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Draft(Editor editor, EntityUid grid) => ((IDictionary) typeof(Editor).GetField("_drafts", Private)!.GetValue(editor)!).Values.Cast<object>()
        .Single(draft => (EntityUid) draft.GetType().GetField("Grid")!.GetValue(draft)! == grid);
    private static Guid Token(object draft) => (Guid) draft.GetType().GetField("Token")!.GetValue(draft)!;
    private static List<GridEditorItem> Items(object draft) => (List<GridEditorItem>) draft.GetType().GetField("Items")!.GetValue(draft)!;
    private static Dictionary<string, EntityUid> Known(object draft) => (Dictionary<string, EntityUid>) draft.GetType().GetField("KnownEntities")!.GetValue(draft)!;
    private static void Apply(Editor editor, ICommonSession player, object draft, List<GridEditorItem> items) => typeof(Editor).GetMethod("Apply", Private)!
        .Invoke(editor, new object[] { new GridEditorApplyEvent(Token(draft), items, patch: true), new EntitySessionEventArgs(player) });

    [Test]
    public async Task NativeSnapshotRoundTripsThroughRealClientNetworkEventsAndBoundedCefChunks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid grid = default, source = default; object draft = null!; string sourceId = "", native = "";
        var description = new string('Ж', 12000) + "\n😀 \"description\"";
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var maps = entities.System<SharedMapSystem>(); maps.CreateMap(out var mapId);
            var created = maps.CreateGridEntity(mapId); grid = created;
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(created, created.Comp, Enumerable.Range(0,4).SelectMany(x => Enumerable.Range(0,2).Select(y => (new Vector2i(x,y),tile))).ToList());
            source = entities.SpawnEntity("ChairWood", new EntityCoordinates(grid,new Vector2(.5f,.5f)));
            entities.System<MetaDataSystem>().SetEntityName(source,"Передаваемый объект");
            entities.System<MetaDataSystem>().SetEntityDescription(source,description);
            var player=pair.Server.PlayerMan.Sessions.Single(); pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var editor=entities.System<Editor>();editor.Open(player,grid);draft=Draft(editor,grid);
            sourceId=Items(draft).Single(item=>item.Kind=="entity").Id;
        });
        await pair.RunTicksSync(30);
        Action<string,string> receive = (type,payload) =>
        {
            if(type!="update")return;using var json=JsonDocument.Parse(payload);
            if(json.RootElement.TryGetProperty("data",out var data)&&data.TryGetProperty("copyVersion",out _))
                native=data.GetProperty("copyItems")[0].GetProperty("entityData").GetString()!;
        };
        GameWebView web=null!;var sending=typeof(GameWebView).GetEvent("Sending",Private)!;
        await pair.Client.WaitAssertion(() =>
        {
            var editor=pair.Client.EntMan.System<ClientEditor>();
            var window=(Content.Client._DeepLagoon.Mapping.GridEditorWindow)typeof(ClientEditor).GetField("_window",Private)!.GetValue(editor)!;
            web=window.Panel.Web;sending.GetAddMethod(true)!.Invoke(web,new object[]{receive});
            var action=typeof(ClientEditor).GetMethod("Action",Private)!;
            action.Invoke(editor,new object[]{"applyStart",JsonSerializer.Serialize(new{id="copy-network",count=1,chunks=1,purpose="copy"})});
            action.Invoke(editor,new object[]{"applyChunk",JsonSerializer.Serialize(new{id="copy-network",index=0,items=new[]{new{id=sourceId,kind="entity",prototype="ChairWood",x=.5,y=.5,rotation=0,anchored=false}}})});
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(native.Length,Is.GreaterThan(8192));
            var text=JsonSerializer.Serialize(new[]{new{id="new_network_copy",kind="entity",prototype="ChairWood",x=2.5,y=.5,rotation=0,anchored=false,entityData=native,entityGroup="network"}});
            var chunks=(text.Length+1023)/1024;var editor=pair.Client.EntMan.System<ClientEditor>();var action=typeof(ClientEditor).GetMethod("Action",Private)!;
            action.Invoke(editor,new object[]{"applyStart",JsonSerializer.Serialize(new{id="paste-network",count=1,chunks,encoding="json"})});
            for(var index=0;index<chunks;index++)
            {
                var payload=JsonSerializer.Serialize(new{id="paste-network",index,text=text.Substring(index*1024,Math.Min(1024,text.Length-index*1024))});
                Assert.That(payload.Length,Is.LessThan(8192));action.Invoke(editor,new object[]{"applyChunk",payload});
            }
            sending.GetRemoveMethod(true)!.Invoke(web,new object[]{receive});
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var uid=Known(draft)["new_network_copy"];Assert.That(uid,Is.Not.EqualTo(source));
            Assert.That(pair.Server.EntMan.GetComponent<MetaDataComponent>(uid).EntityName,Is.EqualTo("Передаваемый объект"));
            Assert.That(pair.Server.EntMan.GetComponent<MetaDataComponent>(uid).EntityDescription,Is.EqualTo(description));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EveryUnaryDeviceVariantConnectsOnEveryEditedLayerAndDisposalUnitsUseTrunks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var placed = new List<(string Id, string Neighbor, int Layer)>();
        object draft = null!;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var maps = entities.System<SharedMapSystem>(); maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var roots = new[] { "GasVentPump", "GasVentScrubber", "GasOutletInjector", "GasPassiveVent" };
            var variants = prototypes.EnumeratePrototypes<EntityPrototype>().Where(proto => !proto.Abstract &&
                prototypes.EnumerateParents(proto, true).Any(parent => roots.Contains(parent.ID))).OrderBy(proto => proto.ID).ToArray();
            Assert.That(variants.Any(proto => proto.ID == "GasVentScrubberAlt2"), Is.True);
            Assert.That(variants.Any(proto => proto.ID == "GasVentPumpVox"), Is.True);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(grid, grid.Comp, Enumerable.Range(0, variants.Length * 2 + 6).SelectMany(x => Enumerable.Range(0, 15).Select(y => (new Vector2i(x, y), floor))).ToList());
            var editor = entities.System<Editor>(); var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player); editor.Open(player, grid); draft = Draft(editor, grid);
            var items = new List<GridEditorItem>();
            foreach (var (prototype, index) in variants.Select((prototype, index) => (prototype, index)))
            for (var layer = 0; layer < 3; layer++)
            {
                var id = $"new_device_{index}_{layer}"; var neighbor = $"new_neighbor_{index}_{layer}";
                items.Add(new() { Id = id, Kind = "entity", Prototype = prototype.ID, X = index * 2 + .5f, Y = layer * 4 + 2.5f, Anchored = true, PipeLayer = layer });
                items.Add(new() { Id = neighbor, Kind = "entity", Prototype = "GasPipeHalf" + new[] { "", "Alt1", "Alt2" }[layer],
                    X = index * 2 + .5f, Y = layer * 4 + 1.5f, Anchored = true, Rotation = 180, PipeLayer = layer });
                placed.Add((id, neighbor, layer));
            }
            foreach (var (proto, index) in new[] { "DisposalUnit", "MailingUnit" }.Select((proto, index) => (proto, index)))
            {
                var x = variants.Length * 2 + index * 2 + .5f;
                items.Add(new() { Id = "new_" + proto, Kind = "entity", Prototype = proto, X = x, Y = 2.5f, Anchored = true });
                items.Add(new() { Id = "new_trunk_" + proto, Kind = "entity", Prototype = "DisposalTrunk", X = x, Y = 2.5f, Anchored = true });
                items.Add(new() { Id = "new_tube_" + proto, Kind = "entity", Prototype = "DisposalPipe", X = x, Y = 1.5f, Anchored = true });
            }
            Apply(editor, player, draft, items);
            Assert.That(items.All(item => Known(draft).ContainsKey(item.Id)), Is.True);
        });
        await pair.RunTicksSync(40);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var known = Known(draft);
            foreach (var (id, neighbor, layer) in placed)
            {
                var devices = entities.GetComponent<NodeContainerComponent>(known[id]).Nodes.Values.OfType<PipeNode>().ToArray();
                var pipe = entities.GetComponent<NodeContainerComponent>(known[neighbor]).Nodes.Values.OfType<PipeNode>().Single();
                Assert.That(devices.All(device => (int) device.CurrentPipeLayer == layer), Is.True, id);
                Assert.That(pipe.NodeGroup, Is.Not.Null, id); Assert.That(devices.Any(device => device.NodeGroup == pipe.NodeGroup), Is.True, id);
            }
            TestContext.Out.WriteLine($"Verified {placed.Count / 3} unary variants on all three layers.");
            foreach (var prototype in new[] { "DisposalUnit", "MailingUnit" })
            {
                var unit = known["new_" + prototype]; var trunk = known["new_trunk_" + prototype]; var tube = known["new_tube_" + prototype];
                Assert.That(entities.GetComponent<TransformComponent>(unit).LocalPosition, Is.EqualTo(entities.GetComponent<TransformComponent>(trunk).LocalPosition));
                var disposal = entities.System<Content.Server.Disposal.Tube.DisposalTubeSystem>();
                Assert.That(disposal.NextTubeFor(trunk, Direction.South), Is.EqualTo(tube));
                Assert.That(disposal.NextTubeFor(tube, Direction.North), Is.EqualTo(trunk));
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("/Maps/_NF/Shuttles/Scrap/scrap-sprinter.yml")]
    [TestCase("/Maps/_NF/Test/dev_map.yml")]
    public async Task CopiesExistingMapMachinerySceneWithTheSameDirectedPipeGraph(string path)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid source = default, target = default; object targetDraft = null!; string before = "";
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var maps = entities.System<SharedMapSystem>(); maps.CreateMap(out var sourceMap); maps.CreateMap(out var targetMap);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGeneric(new ResPath(path), out var loaded,
                new MapLoadOptions { DeserializationOptions = new() { LogOrphanedGrids = false } }), Is.True);
            source = loaded!.Grids.First(); var grid = maps.CreateGridEntity(targetMap); target = grid;
            var player = pair.Server.PlayerMan.Sessions.Single(); pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var editor = entities.System<Editor>(); editor.Open(player, source);
            var sourceDraft = Draft(editor, source); var all = Items(sourceDraft);
            var seed = all.FirstOrDefault(item => item.Prototype is "GasPressurePump" or "GasVolumePump") ?? all.First(item => item.Prototype == "GasVentPump");
            var selection = all.Where(item => Math.Abs(Math.Floor(item.X) - Math.Floor(seed.X)) <= 4 && Math.Abs(Math.Floor(item.Y) - Math.Floor(seed.Y)) <= 4).ToList();
            maps.SetTiles(grid, grid.Comp, selection.Where(item => item.Kind == "tile").Select(item =>
                (new Vector2i((int) item.X, (int) item.Y), new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()[item.Prototype].TileId, item.Flags, item.Variant, item.Mirror))).ToList());
            editor.Open(player, target); targetDraft = Draft(editor, target);
            var errors = new List<string>();
            var copied = (List<GridEditorItem>) typeof(Editor).GetMethod("FreezeSelection", Private)!.Invoke(editor,
                new object[] { sourceDraft, selection.Where(item => item.Kind == "entity").ToList(), errors })!;
            Assert.That(errors, Is.Empty);
            Assert.That(copied.Count, Is.GreaterThan(1));
            foreach (var item in copied) item.Id = "new_real_" + item.Id;
            Apply(editor, player, targetDraft, copied);
            Assert.That(copied.All(item => Known(targetDraft).ContainsKey(item.Id)), Is.True,
                string.Join("\n", copied.Where(item => !Known(targetDraft).ContainsKey(item.Id)).Select(item => $"{item.Prototype} at {item.X},{item.Y}, anchored={item.Anchored}")));
        });
        await pair.RunTicksSync(40);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var editor = entities.System<Editor>();
            var roots = Known(targetDraft).Where(pair => pair.Key.StartsWith("new_real_")).Select(pair => pair.Value).ToArray();
            var sourceIds = roots.Select(uid => entities.GetNetEntity(uid)).ToArray();
            string Graph(IEnumerable<EntityUid> scene)
            {
                var set = scene.ToHashSet();
                string Key(EntityUid uid) { var pos = entities.GetComponent<TransformComponent>(uid).LocalPosition; return FormattableString.Invariant($"{entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID}@{pos.X},{pos.Y}"); }
                return string.Join("\n", set.Where(entities.HasComponent<NodeContainerComponent>).SelectMany(uid => entities.GetComponent<NodeContainerComponent>(uid).Nodes.Values.OfType<PipeNode>()
                    .SelectMany(node => node.ReachableNodes.Where(other => set.Contains(other.Owner)).Select(other => $"{Key(uid)}:{node.Name}->{Key(other.Owner)}:{other.Name}"))).Order());
            }
            var sourceDraft = Draft(editor, source);
            var positions = roots.Select(uid => entities.GetComponent<TransformComponent>(uid).LocalPosition).ToHashSet();
            var originals = Known(sourceDraft).Values.Distinct().Where(uid => positions.Contains(entities.GetComponent<TransformComponent>(uid).LocalPosition)).ToArray();
            before = Graph(originals);
            Assert.That(before.Length, Is.GreaterThan(0), "Existing scene contains a connected pipe section");
            Assert.That(Graph(roots), Is.EqualTo(before), path);
            TestContext.Out.WriteLine($"Copied {roots.Length} entities from {path}; directed pipe graph is unchanged.");
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CopiedSceneKeepsComponentsContainersLayersAndConnectionsThroughDraftAndNativeMap(bool initialized)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid source = default, target = default;
        List<GridEditorItem> copied = null!;
        Dictionary<string, EntityUid> original = new();
        object targetDraft = null!;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var sourceMap, runMapInit: initialized);
            maps.CreateMap(out var targetMap, runMapInit: initialized);
            source = maps.CreateGridEntity(sourceMap); target = maps.CreateGridEntity(targetMap);
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            foreach (var grid in new[] { source, target })
                maps.SetTiles(grid, entities.GetComponent<MapGridComponent>(grid), Enumerable.Range(0, 12).SelectMany(x => Enumerable.Range(0, 8).Select(y => (new Vector2i(x, y), tile))).ToList());
            EntityUid Spawn(string proto, float x, float y)
            {
                var uid = entities.SpawnEntity(proto, new EntityCoordinates(source, new Vector2(x, y)));
                entities.System<SharedTransformSystem>().AnchorEntity(uid);
                original[proto + x] = uid; return uid;
            }
            for (var layer = 0; layer < 3; layer++)
            {
                var suffix = new[] { "", "Alt1", "Alt2" }[layer];
                Spawn("GasPipeStraight" + suffix, .5f, .5f);
                Spawn("GasPipeStraight" + suffix, .5f, 2.5f);
                var uid = Spawn("GasPressurePump", layer + 2.5f, 1.5f);
                // Each pump has its own pair of straight pipes and a different live layer.
                var lower = Spawn("GasPipeStraight" + suffix, layer + 2.5f, .5f);
                var upper = Spawn("GasPipeStraight" + suffix, layer + 2.5f, 2.5f);
                entities.System<AtmosPipeLayersSystem>().SetPipeLayer((uid, entities.GetComponent<AtmosPipeLayersComponent>(uid)), (AtmosPipeLayer) layer);
                var pump = entities.GetComponent<GasPressurePumpComponent>(uid);
                pump.Enabled = layer != 1; pump.TargetPressure = 421 + layer;
                entities.System<MetaDataSystem>().SetEntityName(uid, "Особый насос " + layer);
                entities.System<MetaDataSystem>().SetEntityDescription(uid, "Описание насоса " + layer);
            }
            var volume = Spawn("GasVolumePump", 6.5f, .5f);
            var volumeSettings = entities.GetComponent<GasVolumePumpComponent>(volume);
            volumeSettings.Enabled = true; volumeSettings.TransferRate = 17.25f;
            entities.GetComponent<GasFilterComponent>(Spawn("GasFilter", 5.5f, 2.5f)).Enabled = true;
            typeof(GasMixerComponent).GetField("Enabled")!.SetValue(entities.GetComponent<GasMixerComponent>(Spawn("GasMixer", 6.5f, 2.5f)), true);
            var valve = entities.GetComponent<PressureControlledValveComponent>(Spawn("PressureControlledValve", 7.5f, 2.5f));
            valve.Enabled = false; valve.Threshold = 212.5f;
            var freezer = Spawn("GasThermoMachineFreezer", 7.5f, .5f);
            entities.GetComponent<GasThermoMachineComponent>(freezer).TargetTemperature = 267.5f;
            var trunk = Spawn("DisposalTrunk", 1.5f, 5.5f);
            Spawn("DisposalPipe", 1.5f, 4.5f);
            var mail = Spawn("MailingUnit", 1.5f, 5.5f);
            var mailing = entities.GetComponent<MailingUnitComponent>(mail);
            typeof(MailingUnitComponent).GetField("Tag")!.SetValue(mailing, "LAB-TEST");
            typeof(MailingUnitComponent).GetField("Target")!.SetValue(mailing, "ENGINEERING-TEST");
            entities.GetComponent<DisposalUnitComponent>(mail).AutomaticEngage = false;
            var chair = Spawn("ChairWood", 9.5f, .5f);
            Spawn("WallSolid", 5.5f, 5.5f);
            Spawn("LockerWallMedicalFilled", 5.5f, 5.5f);
            var paper = entities.SpawnEntity("Paper", new EntityCoordinates(source, new Vector2(9.5f, .5f)));
            entities.System<MetaDataSystem>().SetEntityName(paper, "Документ в контейнере");
            Assert.That(entities.System<SharedContainerSystem>().Insert(paper, entities.System<SharedContainerSystem>().EnsureContainer<Container>(chair, "test")), Is.True);
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var editor = entities.System<Editor>();
            editor.Open(player, source); editor.Open(player, target);
            targetDraft = Draft(editor, target);
        });
        await pair.RunTicksSync(35);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var editor = entities.System<Editor>(); var sourceDraft = Draft(editor, source);
            CheckConnections(entities, Known(sourceDraft).Values.Distinct().ToArray());
            CheckSettings(entities, Known(sourceDraft).Values.Distinct());
            var errors = new List<string>();
            copied = (List<GridEditorItem>) typeof(Editor).GetMethod("FreezeSelection", Private)!.Invoke(editor,
                new object[] { sourceDraft, Items(sourceDraft).Where(item => item.Kind == "entity").ToList(), errors })!;
            Assert.That(errors, Is.Empty);
            Assert.That(copied.All(item => item.EntityData.Length > 0), Is.True);
            foreach (var pump in copied.Where(item => item.Prototype == "GasPressurePump"))
                Assert.That(pump.GasPorts, Is.EqualTo(3 << (4 * pump.PipeLayer)), "Compatibility checks must include actual machinery ports on their own layer.");
            foreach (var item in copied) item.Id = "new_copy_" + item.Id;
            // A saved draft must be portable even after its source has been deleted.
            var snapshot = JsonSerializer.Serialize(copied, new JsonSerializerOptions { IncludeFields = true });
            copied = JsonSerializer.Deserialize<List<GridEditorItem>>(snapshot, new JsonSerializerOptions { IncludeFields = true })!;
            var player = pair.Server.PlayerMan.Sessions.Single();
            var savedDraft = editor.ExportDraft(player, Token(targetDraft), Plan(copied));
            using (var reader = new StringReader(savedDraft))
            {
                Assert.That(entities.System<MapLoaderSystem>().TryLoadGeneric(reader, "copied-draft", out var loaded, new MapLoadOptions { DeserializationOptions = new() { LogOrphanedGrids = false } }), Is.True);
                CheckSettings(entities, loaded!.Entities);
            }
            Assert.That(entities.GetComponent<TransformComponent>(original.Values.Single(entities.HasComponent<GasVolumePumpComponent>)).ParentUid, Is.EqualTo(source));
            entities.DeleteEntity(source);
            Apply(editor, player, targetDraft, copied);
            Assert.That(copied.All(item => Known(targetDraft).ContainsKey(item.Id)), Is.True);
            CheckSettings(entities, Known(targetDraft).Where(pair => pair.Key.StartsWith("new_copy_")).Select(pair => pair.Value));
        });
        await pair.RunTicksSync(35);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            CheckSettings(entities, Known(targetDraft).Where(pair => pair.Key.StartsWith("new_copy_")).Select(pair => pair.Value));
            // Move live, already initialized machinery, including the complete pipe routes.
            var moving = JsonSerializer.Deserialize<List<GridEditorItem>>(JsonSerializer.Serialize(Items(targetDraft).Where(item => item.Kind == "entity"),
                new JsonSerializerOptions { IncludeFields = true }), new JsonSerializerOptions { IncludeFields = true })!;
            var identities = moving.ToDictionary(item => item.Id, item => Known(targetDraft)[item.Id]);
            foreach (var item in moving) item.Y += 1;
            Apply(entities.System<Editor>(), pair.Server.PlayerMan.Sessions.Single(), targetDraft, moving);
            foreach (var item in moving)
            {
                Assert.That(Known(targetDraft)[item.Id], Is.EqualTo(identities[item.Id]));
                Assert.That(entities.EntityExists(identities[item.Id]), Is.True);
            }
        });
        await pair.RunTicksSync(35);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var editor = entities.System<Editor>(); var player = pair.Server.PlayerMan.Sessions.Single();
            var roots = Known(targetDraft).Where(pair => pair.Key.StartsWith("new_copy_")).Select(pair => pair.Value).ToArray();
            CheckSettings(entities, roots);
            CheckConnections(entities, roots);
            foreach (var wholeMap in new[] { false, true })
            {
                targetDraft.GetType().GetField("LastDownload")!.SetValue(targetDraft, TimeSpan.Zero);
                var yaml = editor.Export(player, Token(targetDraft), wholeMap, out var error);
                Assert.That(yaml, Is.Not.Null, error);
                using var reader = new StringReader(yaml!);
                Assert.That(entities.System<MapLoaderSystem>().TryLoadGeneric(reader, "copied-native", out var loaded, new MapLoadOptions { DeserializationOptions = new() { LogOrphanedGrids = false } }), Is.True);
                CheckSettings(entities, loaded!.Entities);
            }
            // Invalid entities in the middle of the queue do not prevent later valid placements.
            Apply(editor, player, targetDraft, new()
            {
                new() { Id = "new_before", Kind = "entity", Prototype = "ChairWood", X = 5.5f, Y = 6.5f, Anchored = true },
                new() { Id = "new_invalid", Kind = "entity", Prototype = "MissingGridEditorTestPrototype", X = 6.5f, Y = 6.5f },
                new() { Id = "new_bad_state", Kind = "entity", Prototype = "ChairWood", X = 7.5f, Y = 6.5f, EntityData = "not a valid native scene" },
                new() { Id = "new_outside", Kind = "entity", Prototype = "ChairWood", X = 999.5f, Y = 6.5f },
                new() { Id = "new_bad_component", Kind = "entity", Prototype = "ChairWood", X = 7.5f, Y = 7.5f, EntityGroup = "fault-isolation",
                    EntityData = "root: 2\nentities:\n- proto: ChairWood\n  entities:\n  - uid: 2\n    components:\n    - {type: Lock, locked: invalid-boolean}" },
                new() { Id = "new_after", Kind = "entity", Prototype = "ChairWood", X = 8.5f, Y = 6.5f, Anchored = true, EntityGroup = "fault-isolation",
                    EntityData = "root: 3\nentities:\n- proto: ChairWood\n  entities:\n  - uid: 3\n    components:\n    - {type: Lock, locked: false}" },
            });
            Assert.That(Known(targetDraft).ContainsKey("new_before") && Known(targetDraft).ContainsKey("new_after"), Is.True);
            Assert.That(Known(targetDraft).ContainsKey("new_invalid") || Known(targetDraft).ContainsKey("new_bad_state"), Is.False);
            Assert.That(Known(targetDraft).ContainsKey("new_bad_component"), Is.False);
            Assert.That(entities.GetComponent<Content.Shared.Lock.LockComponent>(Known(targetDraft)["new_after"]).Locked, Is.False);
            var preview = (Dictionary<string, GridEditorItem>) targetDraft.GetType().GetField("Preview")!.GetValue(targetDraft)!;
            Assert.That(preview.Keys, Is.EquivalentTo(new[] { "new_invalid", "new_bad_state", "new_bad_component", "new_outside" }));
            var runningPump = roots.First(uid => entities.HasComponent<GasPressurePumpComponent>(uid) && entities.GetComponent<GasPressurePumpComponent>(uid).Enabled);
            entities.System<SharedTransformSystem>().Unanchor(runningPump);
            Assert.That(entities.GetComponent<GasPressurePumpComponent>(runningPump).Enabled, Is.False, "Ordinary physical unanchoring must still switch a pump off.");
        });
        await pair.CleanReturnAsync();
    }

    private static void CheckSettings(IEntityManager entities, IEnumerable<EntityUid> scene)
    {
        var roots = scene.ToArray();
        Assert.That(roots.Count(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "WallSolid"), Is.EqualTo(1));
        Assert.That(roots.Count(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "LockerWallMedicalFilled"), Is.EqualTo(1));
        for (var layer = 0; layer < 3; layer++)
        {
            var uid = roots.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == "Особый насос " + layer);
            Assert.That(entities.GetComponent<MetaDataComponent>(uid).EntityDescription, Is.EqualTo("Описание насоса " + layer));
            var pump = entities.GetComponent<GasPressurePumpComponent>(uid);
            Assert.That(pump.Enabled, Is.EqualTo(layer != 1)); Assert.That(pump.TargetPressure, Is.EqualTo(421 + layer));
            Assert.That((int) entities.GetComponent<AtmosPipeLayersComponent>(uid).CurrentPipeLayer, Is.EqualTo(layer));
        }
        var volume = roots.Single(uid => entities.HasComponent<GasVolumePumpComponent>(uid));
        Assert.That(entities.GetComponent<GasVolumePumpComponent>(volume).TransferRate, Is.EqualTo(17.25f));
        Assert.That(entities.GetComponent<GasVolumePumpComponent>(volume).Enabled, Is.True);
        Assert.That(entities.GetComponent<GasFilterComponent>(roots.Single(entities.HasComponent<GasFilterComponent>)).Enabled, Is.True);
        Assert.That(entities.GetComponent<GasMixerComponent>(roots.Single(entities.HasComponent<GasMixerComponent>)).Enabled, Is.True);
        var valve = entities.GetComponent<PressureControlledValveComponent>(roots.Single(entities.HasComponent<PressureControlledValveComponent>));
        Assert.That(valve.Enabled, Is.False); Assert.That(valve.Threshold, Is.EqualTo(212.5f));
        var freezer = roots.Single(uid => entities.HasComponent<GasThermoMachineComponent>(uid));
        Assert.That(entities.GetComponent<GasThermoMachineComponent>(freezer).TargetTemperature, Is.EqualTo(267.5f));
        var mail = roots.Single(uid => entities.HasComponent<MailingUnitComponent>(uid));
        Assert.That(entities.GetComponent<MailingUnitComponent>(mail).Tag, Is.EqualTo("LAB-TEST"));
        Assert.That(entities.GetComponent<MailingUnitComponent>(mail).Target, Is.EqualTo("ENGINEERING-TEST"));
        Assert.That(entities.GetComponent<DisposalUnitComponent>(mail).AutomaticEngage, Is.False);
        var chair = roots.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "ChairWood");
        var paper = entities.System<SharedContainerSystem>().GetContainer(chair, "test").ContainedEntities.Single();
        Assert.That(entities.GetComponent<MetaDataComponent>(paper).EntityName, Is.EqualTo("Документ в контейнере"));
    }

    private static void CheckConnections(IEntityManager entities, EntityUid[] roots)
    {
        foreach (var pumpUid in roots.Where(entities.HasComponent<GasPressurePumpComponent>))
        {
            var transform = entities.GetComponent<TransformComponent>(pumpUid);
            var layer = entities.GetComponent<AtmosPipeLayersComponent>(pumpUid).CurrentPipeLayer;
            var pump = entities.GetComponent<NodeContainerComponent>(pumpUid);
            foreach (var (name, y) in new[] { ("inlet", transform.LocalPosition.Y + 1), ("outlet", transform.LocalPosition.Y - 1) })
            {
                var pipe = roots.Single(uid => entities.GetComponent<TransformComponent>(uid).LocalPosition == new Vector2(transform.LocalPosition.X, y));
                var pipeNode = entities.GetComponent<NodeContainerComponent>(pipe).Nodes.Values.OfType<PipeNode>().Single();
                Assert.That(pipeNode.CurrentPipeLayer, Is.EqualTo(layer));
                Assert.That(pipeNode.NodeGroup, Is.Not.Null); Assert.That(pump.Nodes[name].NodeGroup, Is.SameAs(pipeNode.NodeGroup));
            }
        }
    }

    private static string Plan(List<GridEditorItem> items)
    {
        var bytes = new byte[16 * 16 * 7];
        for (var offset = 0; offset < bytes.Length; offset += 7) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), 1);
        var text = new StringBuilder("gridEditorIds:\n");
        for (var i = 0; i < items.Count; i++) text.AppendLine($"  {i + 2}: {items[i].Id}");
        text.AppendLine("gridEditorState:");
        for (var i = 0; i < items.Count; i++) text.AppendLine($"  {i + 2}: {{entityData: {JsonSerializer.Serialize(items[i].EntityData)}, entityGroup: {JsonSerializer.Serialize(items[i].EntityGroup)}, pipeLayer: {items[i].PipeLayer}}}");
        text.AppendLine($"tilemap: {{0: Space, 1: Plating}}\nentities:\n- proto: ''\n  entities:\n  - uid: 1\n    components:\n    - type: MapGrid\n      chunks:\n        0,0: {{ind: '0,0', tiles: '{Convert.ToBase64String(bytes)}', version: 7}}\n    - type: DecalGrid\n      chunkCollection: {{version: 2, nodes: []}}");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            text.AppendLine(FormattableString.Invariant($"- proto: {item.Prototype}\n  entities:\n  - uid: {i + 2}\n    components:\n    - {{type: Transform, parent: 1, pos: '{item.X},{item.Y}', rot: {item.Rotation}, anchored: {item.Anchored.ToString().ToLowerInvariant()}}}"));
        }
        return text.ToString();
    }
}
