using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.Apartments;
using Content.Client._DeepLagoon.WebUI;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentRoomSceneTests
{
    [Test]
    public async Task TerminalShowsItsActualMapAndValidatesAgainstLiveFixtures()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        EntityUid body = default, grid = default;
        Guid token = default;
        List<ApartmentPlacement> layout = new();
        string mappedId = ""; EntityUid mappedEntity = default;
        Vector2i first = default, second = default, occupied = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentBasic");
            // This geometry fixture deliberately keeps the legacy map as a free test template.
            pair.Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic").ExistingOnly = false;
            var player = pair.Server.PlayerMan.Sessions.Single();
            body = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(player, body);
            var system = entities.System<ApartmentSystem>();
            Assert.That(system.Enter(player, player.UserId, out var message), Is.True, message);
            grid = system.Instances[player.UserId].Grid;
            var terminal = entities.EntityQuery<ApartmentTerminalComponent, TransformComponent>()
                .First(item => item.Item2.GridUid == grid).Item1.Owner;
            var foreignTerminal = entities.SpawnEntity("DLApartmentTerminal", outside.GridCoords);
            Assert.That(system.OpenEditor(player, foreignTerminal), Is.False);
            Assert.That(system.OpenEditor(player, terminal), Is.True);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ApartmentEditorSystem>();
            var state = (ApartmentEditorEvent) typeof(ApartmentEditorSystem).GetField("_state", flags)!.GetValue(editor)!;
            Assert.That(state.Grid, Is.EqualTo(pair.Server.EntMan.GetNetEntity(grid)));
            Assert.That(editor.Template!.ID, Is.EqualTo("DLApartmentBasic"));
            var scene = state.Scene!;
            Assert.That(scene.Tiles.Count, Is.EqualTo(129));
            Assert.That(scene.Tiles.Count, Is.LessThan(scene.Width * scene.Height));
            Assert.That(scene.Objects.Any(item => item.Prototype == "N14Bed"), Is.False);
            Assert.That(state.MapFurniture.Any(item => item.Prototype == "N14Bed"), Is.True);
            Assert.That(state.MapFurniture.Any(item => item.Prototype == "SmartFridge"), Is.True);
            Assert.That(state.MapFurniture.Any(item => item.Prototype.Contains("Wall") || item.Prototype.Contains("Airlock") || item.Prototype == "DLApartmentElevatorPanel"), Is.False);
            layout = state.Layout.Select(item => item.Copy()).ToList();
            mappedId = state.MapFurniture.First(item => item.Prototype == "PottedPlant2").Baseline.Id;
            Assert.That(scene.Objects.Any(item => item.Prototype == "DLApartmentElevatorPanel"), Is.True);
            Assert.That(scene.Objects.Any(item => item.Prototype == "DLApartmentTerminal"), Is.True);
            Assert.That(scene.Occupied, Is.Not.Empty);
            token = state.Token;
            var free = scene.Tiles.Select(tile => new Vector2i((int) tile.X, (int) tile.Y))
                .Where(cell => !scene.Occupied.Contains(cell) && !layout.Any(item => item.X == cell.X && item.Y == cell.Y) && !editor.Template.ReservedCells.Contains(cell) && cell != editor.Template.Arrival).ToArray();
            Assert.That(free.Length, Is.GreaterThan(1));
            first = free[0]; second = free[1];
            occupied = scene.Occupied.First(cell => cell != editor.Template.Arrival && !editor.Template.ReservedCells.Contains(cell));
            var window = (ApartmentEditorWindow) typeof(ApartmentEditorSystem).GetField("_window", flags)!.GetValue(editor)!;
            var json = (string) typeof(TguiPanel).GetField("_data", flags)!.GetValue(window.Panel)!;
            var preview = NativeUiData.Parse(json);
            Assert.That(preview.Object("scene").Array("objects").Count(), Is.EqualTo(scene.Objects.Count));
            Assert.That(preview.Array("catalog").All(item => item.Array("images").Any()), Is.True, "Every furniture entry needs an actual game sprite.");
            var walls = preview.Object("scene").Array("objects").Where(item => item.Bool("wall")).ToArray();
            Assert.That(walls, Is.Not.Empty);
            Assert.That(walls.All(item => item.Array("images").Any()), Is.True);
            Assert.That(walls.SelectMany(item => item.Array("images")).All(image => image.String("url").StartsWith("data:image/png;base64,")), Is.True);
            var fixtureDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", ".local", "codex", "apartment-interior");
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllText(Path.Combine(fixtureDirectory, "apartment-room-preview.json"), json);
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var player = pair.Server.PlayerMan.Sessions.Single();
            var system = entities.System<ApartmentSystem>();
            mappedEntity = system.Instances[player.UserId].Furniture[mappedId];
            var containers = entities.System<SharedContainerSystem>();
            var stash = containers.GetContainer(mappedEntity, "stash");
            var keepsake = entities.SpawnEntity("Paper", new EntityCoordinates(grid, Vector2.One));
            Assert.That(containers.Insert(keepsake, stash), Is.True);
            var original = layout.Single(item => item.Id == mappedId);
            original.X = first.X; original.Y = first.Y; original.Rotation = 0;
            layout.Add(new ApartmentPlacement { Id = "added_scene_chair", Furniture = "DLAptChair", X = second.X, Y = second.Y });
            Assert.That(system.Apply(player, token, 0, layout), Is.Null);
            Assert.That(system.Instances[player.UserId].Furniture[mappedId], Is.EqualTo(mappedEntity));
            Assert.That(stash.ContainedEntities, Does.Contain(keepsake));
            Assert.That(entities.GetComponent<TransformComponent>(keepsake).ParentUid, Is.EqualTo(mappedEntity));
            Assert.That(entities.GetComponent<TransformComponent>(mappedEntity).LocalPosition, Is.EqualTo(new Vector2(first.X + .5f, first.Y + .5f)));
            Assert.That(system.Instances[player.UserId].Delta.Changed.Single().Id, Is.EqualTo(mappedId));
            Assert.That(system.Instances[player.UserId].Delta.Added.Single().Id, Is.EqualTo("added_scene_chair"));
            var chair = system.Instances[player.UserId].Furniture["added_scene_chair"];
            var transform = entities.System<SharedTransformSystem>();
            transform.SetCoordinates(chair, new EntityCoordinates(grid, new Vector2(second.X + .5f, second.Y + .5f)));
            transform.AnchorEntity(chair);
            Assert.That(system.OpenEditor(player), Is.True);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ApartmentEditorSystem>();
            Assert.That(editor.Draft.Single(item => item.Id == "added_scene_chair").X, Is.EqualTo(second.X));
            Assert.That(editor.Draft.Single(item => item.Id == "added_scene_chair").Y, Is.EqualTo(second.Y));
            var state = (ApartmentEditorEvent) typeof(ApartmentEditorSystem).GetField("_state", flags)!.GetValue(editor)!;
            token = state.Token;
            layout = state.Layout.Select(item => item.Copy()).ToList();
            Assert.That(state.Scene!.Objects.Any(item => item.Id == "e:" + state.Entities["added_scene_chair"]), Is.False);
        });
        await Task.Delay(600);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var player = pair.Server.PlayerMan.Sessions.Single();
            var system = entities.System<ApartmentSystem>();
            var added = layout.Single(item => item.Id == "added_scene_chair"); added.X = occupied.X; added.Y = occupied.Y;
            Assert.That(system.Apply(player, token, 1, layout), Does.Contain("стационарным"));
            system.Leave(player.UserId, out _);
            var reloaded = (ApartmentSystem.Instance) typeof(ApartmentSystem).GetMethod("Create", flags)!.Invoke(system, new object[] { player.UserId, "DLApartmentBasic" })!;
            var restored = entities.GetComponent<TransformComponent>(reloaded.Furniture[mappedId]);
            Assert.That(restored.LocalPosition, Is.EqualTo(new Vector2(first.X + .5f, first.Y + .5f)));
            Assert.That(restored.LocalRotation.Degrees, Is.EqualTo(0).Within(.01));
            Assert.That(entities.GetComponent<MetaDataComponent>(reloaded.Furniture[mappedId]).EntityPrototype!.ID, Is.EqualTo("PottedPlant2"));
            entities.DeleteEntity(reloaded.Grid);
            pair.Server.PlayerMan.SetAttachedEntity(player, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }
}
