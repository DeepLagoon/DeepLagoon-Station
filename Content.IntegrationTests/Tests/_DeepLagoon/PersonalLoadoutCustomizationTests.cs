using System.Linq;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Client.Inventory;
using Robust.Client.GameObjects;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PersonalLoadoutCustomizationTests
{
    [TestCase("ClothingOuterHardsuitSpatio", "outerClothing", 3)]
    [TestCase("ClothingModsuitStandard", "back", 4)]
    public async Task PaintIncludesAttachedPartsInPreviewAndStartingGear(string prototype, string slot, int partCount)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var painted = Color.FromHex("#1188cc");
        var repainted = Color.FromHex("#cc4411");
        EntityUid clientMob = default;
        EntityUid clientItem = default;
        EntityUid serverMob = default;

        EntityUid Equip(IEntityManager entities, string mobPrototype)
        {
            var mob = entities.SpawnEntity(mobPrototype, Robust.Shared.Map.MapCoordinates.Nullspace);
            entities.System<SharedStationSpawningSystem>().EquipStartingGear(mob,
                new StartingGearPrototype { Equipment = new() { [slot] = prototype } },
                customization: new PersonalLoadoutCustomization { Color = "#1188cc", Name = "Личный скафандр" });
            var inventory = entities.System<InventorySystem>();
            Assert.That(inventory.TryGetSlotEntity(mob, slot, out var item), Is.True);
            var parts = entities.System<ToggleableClothingSystem>().GetAttachedClothingsList(item.Value);
            Assert.That(parts, Has.Count.EqualTo(partCount));
            CheckColor(entities, item.Value, painted);
            foreach (var part in parts)
            {
                CheckColor(entities, part, painted);
                Assert.That(entities.GetComponent<MetaDataComponent>(part).EntityName, Is.Not.EqualTo("Личный скафандр"));
            }
            // Deploy the actual linked pieces through the same action used in game.
            entities.EventBus.RaiseLocalEvent(item.Value, new ToggleClothingEvent { Performer = mob });
            Assert.That(inventory.TryGetSlotEntity(mob, "head", out var helmet), Is.True);
            Assert.That(parts, Does.Contain(helmet.Value));
            return mob;
        }

        void CheckColor(IEntityManager entities, EntityUid item, Color expected)
        {
            Assert.That(entities.System<SharedAppearanceSystem>().TryGetData<Color>(item, PersonalLoadoutVisuals.Color, out var color), Is.True);
            Assert.That(color, Is.EqualTo(expected));
        }

        void CheckPreview(Color expected)
        {
            var entities = pair.Client.EntMan;
            var inventory = entities.System<InventorySystem>();
            var wearer = entities.GetComponent<SpriteComponent>(clientMob);
            var slots = entities.GetComponent<InventorySlotsComponent>(clientMob);
            var parts = entities.System<ToggleableClothingSystem>().GetAttachedClothingsList(clientItem);
            foreach (var part in parts.Append(clientItem))
            {
                CheckColor(entities, part, expected);
                if (entities.TryGetComponent<SpriteComponent>(part, out var sprite))
                    Assert.That(sprite.Color, Is.EqualTo(expected));
            }
            foreach (var wornSlot in new[] { slot, "head" })
            {
                Assert.That(inventory.TryGetSlotEntity(clientMob, wornSlot, out _), Is.True);
                Assert.That(slots.VisualLayerKeys.TryGetValue(wornSlot, out var layers), Is.True);
                Assert.That(layers, Is.Not.Empty);
                foreach (var layer in layers)
                    Assert.That(wearer[wearer.LayerMapGet(layer)].Color, Is.EqualTo(expected));
            }
        }

        await pair.Server.WaitAssertion(() => serverMob = Equip(pair.Server.EntMan, "MobHuman"));
        await pair.Client.WaitAssertion(() =>
        {
            clientMob = Equip(pair.Client.EntMan, "MobHumanDummy");
            Assert.That(pair.Client.EntMan.System<InventorySystem>().TryGetSlotEntity(clientMob, slot, out var item), Is.True);
            clientItem = item.Value;
        });
        await pair.RunTicksSync(2);
        await pair.Client.WaitAssertion(() =>
        {
            CheckPreview(painted);
            pair.Client.EntMan.System<PersonalLoadoutEquipSystem>().Customize(clientItem,
                new PersonalLoadoutCustomization { Color = "#cc4411" });
        });
        await pair.RunTicksSync(2);
        await pair.Client.WaitAssertion(() =>
        {
            CheckPreview(repainted);
            pair.Client.EntMan.DeleteEntity(clientMob);
        });
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(serverMob));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EntireCataloguePreviewDoesNotStartGameplayEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var count = entities.EntityCount;
            var images = new TguiSpriteImages();
            foreach (var id in prototypes.EnumeratePrototypes<LoadoutPrototype>().SelectMany(p => p.PersonalItems).Distinct())
                _ = images.Item(id.Id).ToArray();
            Assert.That(entities.EntityCount, Is.EqualTo(count));
            Assert.That(images.Item("ClothingUniformJumpsuitSuitBlack").Any(image => image.ToString().Contains("data:image/png;base64,")), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PaintAndTextBelongToSpawnedItemAndDoNotChangeAnotherCharacter()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        await server.WaitAssertion(() =>
        {
            server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = server.ResolveDependency<IEntityManager>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            const string id = "DLEELoadoutClothingJumpsuitSuitBlack";
            var role = new RoleLoadout(PersonalLoadoutSystem.Role);
            var group = prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups
                .First(g => prototypes.Index(g).Loadouts.Any(l => l.Id == id));
            role.SelectedLoadouts[group] = new() { new() { Prototype = id, Customization = new()
            { Color = "#1188cc", Name = "Личный комбинезон", Description = "Тестовое описание" } } };
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(role);
            var plainRole = role.Clone();
            plainRole.SelectedLoadouts[group][0] = new Loadout { Prototype = id };
            var plainProfile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(plainRole);
            var first = entities.SpawnEntity("MobHuman", Robust.Shared.Map.MapCoordinates.Nullspace);
            var second = entities.SpawnEntity("MobHuman", Robust.Shared.Map.MapCoordinates.Nullspace);
            try
            {
                var equip = entities.System<PersonalLoadoutEquipSystem>();
                equip.Apply(first, profile, "Passenger", null, true);
                equip.Apply(second, plainProfile, "Passenger", null, true);
                var inventory = entities.System<InventorySystem>();
                Assert.That(inventory.TryGetSlotEntity(first, "jumpsuit", out var firstItem), Is.True);
                Assert.That(inventory.TryGetSlotEntity(second, "jumpsuit", out var secondItem), Is.True);
                Assert.That(firstItem, Is.Not.EqualTo(secondItem));
                var metadata = entities.GetComponent<MetaDataComponent>(firstItem.Value);
                Assert.That(metadata.EntityName, Is.EqualTo("Личный комбинезон"));
                Assert.That(metadata.EntityDescription, Is.EqualTo("Тестовое описание"));
                Assert.That(entities.System<SharedAppearanceSystem>().TryGetData<Color>(firstItem.Value, PersonalLoadoutVisuals.Color, out var color), Is.True);
                Assert.That(color, Is.EqualTo(Color.FromHex("#1188cc")));
                Assert.That(entities.HasComponent<PersonalLoadoutVisualsComponent>(secondItem.Value), Is.False);
                Assert.That(entities.GetComponent<MetaDataComponent>(secondItem.Value).EntityName,
                    Is.EqualTo(prototypes.Index<EntityPrototype>("ClothingUniformJumpsuitSuitBlack").Name));
            }
            finally { entities.DeleteEntity(first); entities.DeleteEntity(second); }
        });
        await pair.CleanReturnAsync();
    }
}
