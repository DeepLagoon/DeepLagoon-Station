using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client.Lock.Visualizers;
using Content.Client.Storage.Visualizers;
using Content.Client.Trigger;
using Content.Client.PDA;
using Content.Shared.PDA;
using Content.Shared.Storage.Components;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server._DeepLagoon.Mapping;
using Content.Server.Storage.Components;
using Content.Server.Storage.EntitySystems;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Light.Components;
using Content.Shared.Lock;
using Content.Shared.Physics;
using Content.Shared.Storage;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunMapLoadingTests
{
    [Test]
    public async Task ImportedSpritesKeepTheirRuntimeLayers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var sprites = entities.System<SpriteSystem>();
            var appearance = entities.System<AppearanceSystem>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<EntityPrototype>().ToArray();

            Entity<SpriteComponent> Spawn(EntityPrototype prototype)
            {
                var uid = entities.SpawnEntity(prototype.ID, MapCoordinates.Nullspace);
                return (uid, entities.GetComponent<SpriteComponent>(uid));
            }

            var storage = Spawn(prototypes.Single(p => p.ID == "BitrunMapMachinerySuitStorageUnit"));
            Assert.That(sprites.TryGetLayer(storage, StorageVisualLayers.Door, out var door, true), Is.True);
            Assert.That(sprites.TryGetLayer(storage, LockVisualLayers.Lock, out var indicator, true), Is.True);
            Assert.That(sprites.LayerGetRsiState(storage, 0).Name, Is.EqualTo("classic"));
            appearance.SetData(storage, StorageVisuals.Open, false);
            appearance.SetData(storage, LockVisuals.Locked, true);
            appearance.OnChangeData(storage, storage.Comp);
            Assert.That(indicator!.Visible, Is.True);
            Assert.That(indicator.State.Name, Is.EqualTo("classic_locked"));
            appearance.SetData(storage, StorageVisuals.Open, true);
            appearance.OnChangeData(storage, storage.Comp);
            Assert.That(door!.Visible, Is.True);
            Assert.That(indicator.Visible, Is.False);
            Assert.That(door.State.Name, Is.EqualTo("classic_open"));
            Assert.That(sprites.LayerGetRsiState(storage, 0).Name, Is.EqualTo("classic"));
            appearance.SetData(storage, StorageVisuals.Open, false);
            appearance.SetData(storage, LockVisuals.Locked, false);
            appearance.OnChangeData(storage, storage.Comp);
            Assert.That(door.Visible, Is.False);
            Assert.That(indicator.State.Name, Is.EqualTo("lock_indicator_off"));
            entities.DeleteEntity(storage);

            foreach (var prototype in prototypes.Where(p => p.ID.StartsWith("BitrunMapItemModularComputerPda")))
            {
                var pda = Spawn(prototype);
                var rsi = sprites.LayerGetEffectiveRsi(pda, 0)!;
                appearance.OnChangeData(pda, pda.Comp);
                Assert.That(rsi.TryGetState(sprites.LayerGetRsiState(pda, 0), out _), Is.True, prototype.ID);
                entities.DeleteEntity(pda);
            }

            foreach (var prototype in prototypes.Where(p => p.ID.StartsWith("BitrunMapItemGrenade") ||
                                                           p.ID.StartsWith("BitrunMapItemStorageBagTrash")))
            {
                var item = Spawn(prototype);
                var rsi = sprites.LayerGetEffectiveRsi(item, 0)!;
                if (entities.TryGetComponent<TimerTriggerVisualsComponent>(item, out var trigger))
                {
                    Assert.That(rsi.TryGetState(trigger.UnprimedSprite, out _), Is.True, prototype.ID);
                    Assert.That(rsi.TryGetState(trigger.PrimingSprite, out _), Is.True, prototype.ID);
                }
                if (entities.TryGetComponent<StorageFillVisualizerComponent>(item, out var fill))
                {
                    for (var level = 0; level <= fill.MaxFillLevels; level++)
                        Assert.That(rsi.TryGetState($"{fill.FillBaseName}-{level}", out _), Is.True, prototype.ID);
                }
                entities.DeleteEntity(item);
            }

            var crabs = prototypes.Where(p => p.ID.StartsWith("BitrunMapMobLivingBasicCrab")).ToArray();
            Assert.That(crabs, Is.Not.Empty);
            foreach (var prototype in crabs)
            {
                var crab = Spawn(prototype);
                Assert.That(sprites.LayerGetEffectiveRsi(crab, 0)?.TryGetState("crab-moving", out _), Is.True);
                entities.DeleteEntity(crab);
            }

            var table = Spawn(prototypes.Single(p => p.ID == "BitrunMapStructureTableWood"));
            var salt = Spawn(prototypes.Single(p => p.ID.StartsWith("BitrunMapItemReagentContainersCondimentSaltshaker") &&
                                                     p.Parents?.Contains("FoodShakerSalt") == true));
            Assert.That(salt.Comp.DrawDepth, Is.GreaterThan(table.Comp.DrawDepth));
            entities.DeleteEntity(salt);
            entities.DeleteEntity(table);

            var cocktail = Spawn(prototypes.First(p => p.ID.StartsWith("BitrunMapItemReagentContainersCupGlassDrinkingglassFilledPinaColada")));
            Assert.That(cocktail.Comp.Offset, Is.Not.EqualTo(Vector2.Zero));
            entities.DeleteEntity(cocktail);

            var plants = prototypes.Where(p => p.ID.StartsWith("BitrunMapItemKirbyplants") &&
                                              p.Parents?.Contains("PottedPlant0") == true).ToArray();
            Assert.That(plants, Is.Not.Empty);
            foreach (var prototype in plants)
            {
                var plant = Spawn(prototype);
                Assert.That(sprites.LayerGetRsiState(plant, 0).Name, Is.Not.EqualTo("random_plant"));
                entities.DeleteEntity(plant);
            }

            var cable = Spawn(prototypes.Single(p => p.ID == "CableApcExtension"));
            var cableRsi = sprites.LayerGetEffectiveRsi(cable, 0)!;
            for (var mask = 0; mask < 16; mask++)
                Assert.That(cableRsi.TryGetState($"lvcable_{mask}", out _), Is.True);
            entities.DeleteEntity(cable);

            var apc = Spawn(prototypes.Single(p => p.ID == "BitrunMapMachineryPowerApcAutoNameDirectionalNorthSpriteOffset00Point78125"));
            Assert.That(apc.Comp.SnapCardinals, Is.True);
            Assert.That(apc.Comp.Offset, Is.EqualTo(new Vector2(0, 25f / 32f)));
            entities.DeleteEntity(apc);

            var wideDoor = Spawn(prototypes.Single(p => p.ID == "BitrunMapMachineryDoorAirlockMultiTileMetalSpriteOffset0Point50Point5"));
            Assert.That(wideDoor.Comp.NoRotation, Is.True);
            Assert.That(wideDoor.Comp.SnapCardinals, Is.False);
            sprites.GetLocalBounds(wideDoor);
            entities.DeleteEntity(wideDoor);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BeachBarWoodenDoorsBlockMovementUntilOpened()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
            Destructive = true,
        });
        var server = pair.Server;
        var entities = server.EntMan;
        EntityUid map = default;
        EntityUid[] doors = [];
        await server.WaitAssertion(() =>
        {
            map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TryLoadGridTracked(mapId,
                new("/Maps/_DeepLagoon/Bitrunning/Domains/beach_bar.yml"), out var loaded), Is.True);
            var poster = entities.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            var colaPosterMounted = false;
            while (poster.MoveNext(out _, out var metadata, out var transform))
            {
                if (transform.GridUid != loaded!.Value.Owner || metadata.EntityName != "Space Cola")
                    continue;
                colaPosterMounted |= transform.LocalPosition == new Vector2(17.5f, 31.5f);
            }
            Assert.That(colaPosterMounted, Is.True);
            var woodDoors = FindMarkers<DoorComponent>(entities, loaded!.Value.Owner)
                .Where(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.Parents?.Contains("WoodDoor") == true);
            doors = woodDoors.ToArray();
            Assert.That(doors, Has.Length.EqualTo(2));
            foreach (var door in doors)
            {
                Assert.That(entities.GetComponent<DoorComponent>(door).State, Is.EqualTo(DoorState.Closed));
                Assert.That(entities.GetComponent<PhysicsComponent>(door).CanCollide, Is.True);
                Assert.That(entities.GetComponent<FixturesComponent>(door).Fixtures.Values.Any(fixture =>
                    fixture.Hard && (fixture.CollisionLayer & (int) CollisionGroup.MobMask) != 0), Is.True);
                Assert.That(entities.System<SharedDoorSystem>().TryOpen(door), Is.True);
            }
        });
        await server.WaitRunTicks(100);
        await server.WaitAssertion(() =>
        {
            foreach (var door in doors)
            {
                Assert.That(entities.GetComponent<DoorComponent>(door).State, Is.EqualTo(DoorState.Open));
                Assert.That(entities.GetComponent<PhysicsComponent>(door).CanCollide, Is.False);
            }
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Domains", 26)]
    [TestCase("Safehouses", 14)]
    public async Task ImportedGridsLoadWithTheirMarkers(string folder, int count)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
            Destructive = true,
        });
        await pair.Server.WaitAssertion(() =>
        {
            var resources = pair.Server.ResolveDependency<IResourceManager>();
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var loader = entities.System<MapLoaderSystem>();
            var files = resources.ContentFindFiles($"/Maps/_DeepLagoon/Bitrunning/{folder}")
                .Where(file => file.Extension == "yml").OrderBy(file => file.ToString()).ToArray();
            Assert.That(files, Has.Length.EqualTo(count));

            foreach (var file in files)
            {
                var map = maps.CreateMap(out var mapId);
                try
                {
                    Assert.That(loader.TryLoadGridTracked(mapId, file, out var loaded), Is.True, file.ToString());
                    var grid = loaded!.Value;
                    var attackerMarkers = FindMarkers<BitrunAvatarSpawnComponent>(entities, grid.Owner);
                    var defenderMarkers = FindMarkers<BitrunCacheSpawnComponent>(entities, grid.Owner);
                    Assert.Multiple(() =>
                    {
                        Assert.That(maps.GetAllTiles(grid.Owner, grid.Comp).Any(), Is.True, file.ToString());
                        Assert.That(entities.GetComponent<GridEnvironmentComponent>(grid.Owner).SafeAtmosphere, Is.True,
                            file.ToString());
                        Assert.That(entities.GetComponent<ImplicitRoofComponent>(grid.Owner).Color, Is.EqualTo(Color.White),
                            file.ToString());
                        Assert.That(attackerMarkers, Is.Not.Empty, file.ToString());
                        Assert.That(HasMarker<BitrunExitComponent>(entities, grid.Owner), Is.True, file.ToString());
                        Assert.That(HasMarker<BitrunCacheGoalComponent>(entities, grid.Owner), Is.True, file.ToString());
                        if (folder == "Domains")
                            Assert.That(defenderMarkers, Is.Not.Empty, file.ToString());
                    });

                    if (folder == "Domains" && attackerMarkers.Count > 0 && defenderMarkers.Count > 0)
                    {
                        var (attackers, defenders) = entities.System<BitrunSystem>().FindTeamSpawns(grid, attackerMarkers, defenderMarkers);
                        Assert.Multiple(() =>
                        {
                            Assert.That(attackers, Is.Not.Empty, file.ToString());
                            Assert.That(defenders, Is.Not.Empty, file.ToString());
                            Assert.That(attackers.Intersect(defenders), Is.Empty, file.ToString());
                        });
                    }

                }
                finally
                {
                    entities.DeleteEntity(map);
                }
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AncientMilsimImportsCrateContentsTwoTileDoorAndCompatibleMagazine()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TryLoadGridTracked(mapId,
                new("/Maps/_DeepLagoon/Bitrunning/Domains/skyrat_ancientmilsim.yml"), out var loaded), Is.True);
            AssertAncientMilsimImport(entities, loaded!.Value.Owner);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    private static void AssertAncientMilsimImport(IEntityManager entities, EntityUid grid)
    {
        var xform = entities.System<SharedTransformSystem>();
        var crates = FindMarkers<ContainerManagerComponent>(entities, grid);
        var crate = crates.Single(uid => entities.GetComponent<TransformComponent>(uid).LocalPosition == new Vector2(36.5f, 34.5f)
                                         && entities.HasComponent<EntityStorageComponent>(uid));
        var stored = entities.System<SharedContainerSystem>().GetContainer(crate, "entity_storage").ContainedEntities.ToArray();
        // Thirteen authored PopulateContents items plus two map-authored items.
        Assert.That(stored, Has.Length.EqualTo(15));
        entities.System<EntityStorageSystem>().OpenStorage(crate);
        Assert.That(stored.All(uid => entities.GetComponent<TransformComponent>(uid).ParentUid == grid), Is.True);

        var door = FindMarkers<DoorComponent>(entities, grid).Single(uid =>
            entities.GetComponent<TransformComponent>(uid).LocalPosition == new Vector2(14.5f, 20.5f));
        var bounds = entities.System<SharedPhysicsSystem>().GetWorldAABB(door);
        Assert.That(bounds.Contains(xform.GetWorldPosition(door) + Vector2.UnitY), Is.True,
            $"Door at {xform.GetWorldPosition(door)} with local rotation {entities.GetComponent<TransformComponent>(door).LocalRotation.Degrees} has bounds {bounds}.");
        Assert.That(bounds.Contains(xform.GetWorldPosition(door) + Vector2.UnitX), Is.False);

        var native = FindMarkers<MetaDataComponent>(entities, grid);
        var drozd = native.First(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID
            .StartsWith("BitrunMapItemGunBallisticAutomaticLanca") == true);
        var magazine = native.First(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID
            .StartsWith("BitrunMapItemAmmoBoxMagazineLanca") == true);
        var slots = entities.System<ItemSlotsSystem>();
        Assert.That(slots.TryEject(drozd, "gun_magazine", null, out _), Is.True);
        Assert.That(slots.TryInsert(drozd, "gun_magazine", magazine, null), Is.True);
    }

    private static bool HasMarker<T>(IEntityManager entities, EntityUid grid)
        where T : IComponent => FindMarkers<T>(entities, grid).Count != 0;

    private static List<EntityUid> FindMarkers<T>(IEntityManager entities, EntityUid grid)
        where T : IComponent
    {
        var result = new List<EntityUid>();
        var query = entities.AllEntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.GridUid == grid)
                result.Add(uid);
        }

        return result;
    }
}
