using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client.IconSmoothing;
using Content.Server.Administration.Managers;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Editor = Content.Server._DeepLagoon.Mapping.GridEditorSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GridEditorSmoothingTests
{
    [Test]
    public async Task AppliedTablesConnectAndRefreshAfterMoving()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true },
            new Robust.UnitTesting.Pool.ExternalTestContext(nameof(AppliedTablesConnectAndRefreshAfterMoving), TestContext.Progress));
        var tables = new List<NetEntity>();
        EntityUid grid = default;
        Guid token = default;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var player = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(player);
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId);
            var mapGrid = map.CreateGridEntity(mapId);
            grid = mapGrid.Owner;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            map.SetTiles(grid, mapGrid.Comp, Enumerable.Range(0, 5).Select(x => (new Vector2i(x, 0), floor)).ToList());
            var body = entities.SpawnEntity("MobObserver", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            pair.Server.PlayerMan.SetAttachedEntity(player, body);
            var editor = entities.System<Editor>();
            Assert.That(editor.Open(player, grid), Does.Contain("открыт"));
            var draft = ((IDictionary) typeof(Editor).GetField("_drafts", flags)!.GetValue(editor)!)[player]!;
            token = (Guid) draft.GetType().GetField("Token")!.GetValue(draft)!;
            typeof(Editor).GetMethod("Apply", flags)!.Invoke(editor, new object[] {
                new GridEditorApplyEvent(token, new() {
                    new() { Id = "new_left", Kind = "entity", Prototype = "TableWood", X = .5f, Y = .5f, Anchored = true },
                    new() { Id = "new_right", Kind = "entity", Prototype = "TableWood", X = 1.5f, Y = .5f, Anchored = true }
                }, patch: true), new EntitySessionEventArgs(player) });
            var known = (Dictionary<string, EntityUid>) draft.GetType().GetField("KnownEntities")!.GetValue(draft)!;
            tables.Add(entities.GetNetEntity(known["new_left"]));
            tables.Add(entities.GetNetEntity(known["new_right"]));
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            entities.System<IconSmoothSystem>().FrameUpdate(0);
            foreach (var table in tables)
            {
                var uid = entities.GetEntity(table);
                Assert.That(entities.GetComponent<TransformComponent>(uid).Anchored, Is.True);
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                Assert.That(sprite.AllLayers.Any(layer => layer.RsiState.Name is "state_1" or "state_4"), Is.True,
                    $"Table {table} must connect: {string.Join(",", sprite.AllLayers.Select(layer => layer.RsiState.Name))}");
            }
        });
        foreach (var x in new[] { 3.5f, 1.5f })
        {
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var player = pair.Server.PlayerMan.Sessions.Single();
                typeof(Editor).GetMethod("Apply", flags)!.Invoke(entities.System<Editor>(), new object[] {
                    new GridEditorApplyEvent(token, new() {
                        new() { Id = "e:" + tables[1], Kind = "entity", Prototype = "TableWood", X = x, Y = .5f, Anchored = true }
                    }, patch: true), new EntitySessionEventArgs(player) });
            });
            await pair.RunTicksSync(30);
            await pair.Client.WaitAssertion(() =>
            {
                var entities = pair.Client.EntMan;
                entities.System<IconSmoothSystem>().FrameUpdate(0);
                foreach (var table in tables)
                {
                    var sprite = entities.GetComponent<SpriteComponent>(entities.GetEntity(table));
                    Assert.That(sprite.AllLayers.Any(layer => layer.RsiState.Name is "state_1" or "state_4"), Is.EqualTo(x == 1.5f),
                        $"Table {table} after moving to {x}: {string.Join(",", sprite.AllLayers.Select(layer => layer.RsiState.Name))}");
                }
            });
        }
        await pair.Server.WaitAssertion(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }
}
