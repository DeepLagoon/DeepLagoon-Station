using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Whitelist;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class LegacyMagnumWeaponsTests
{
    [Test]
    public async Task LegacyMagnumWeaponsSpawnAndSupplyCompatibleAmmunition()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var slots = entities.System<ItemSlotsSystem>();
            var whitelists = entities.System<EntityWhitelistSystem>();
            var cartridge = entities.SpawnEntity("CartridgeMagnum", map.GridCoords);
            var loader = entities.SpawnEntity("SpeedLoaderMagnum", map.GridCoords);

            foreach (var prototype in new[] { "WeaponPistolN1984", "WeaponPistolN1984Security", "WeaponPistolN1984Empty" })
            {
                var gun = entities.SpawnEntity(prototype, map.GridCoords);
                Assert.That(slots.TryGetSlot(gun, "gun_magazine", out var magazineSlot), Is.True);
                Assert.That(slots.TryGetSlot(gun, "gun_chamber", out var chamberSlot), Is.True);
                Assert.That(whitelists.IsValid(chamberSlot!.Whitelist!, cartridge), Is.True, prototype);

                if (prototype == "WeaponPistolN1984Empty")
                {
                    Assert.That(magazineSlot!.HasItem, Is.False);
                    Assert.That(chamberSlot.HasItem, Is.False);
                    var magazine = entities.SpawnEntity("MagazineMagnum", map.GridCoords);
                    Assert.That(slots.TryInsert(gun, "gun_magazine", magazine, null), Is.True);
                }
                else
                {
                    Assert.That(magazineSlot!.Item, Is.Not.Null);
                    Assert.That(entities.GetComponent<MetaDataComponent>(magazineSlot.Item!.Value).EntityPrototype?.ID,
                        Is.EqualTo("MagazineMagnum"));
                    Assert.That(chamberSlot.Item, Is.Not.Null);
                    Assert.That(entities.GetComponent<MetaDataComponent>(chamberSlot.Item!.Value).EntityPrototype?.ID,
                        Is.EqualTo("CartridgeMagnum"));
                }

                VerifyAmmunition(gun);
                entities.DeleteEntity(gun);
            }

            foreach (var prototype in new[] { "WeaponRevolverDeckard", "WeaponRevolverDeckardSecurity", "WeaponRevolverFitz", "WeaponRevolverFitzSecurity" })
            {
                var gun = entities.SpawnEntity(prototype, map.GridCoords);
                var provider = entities.GetComponent<RevolverAmmoProviderComponent>(gun);
                Assert.That(whitelists.IsValid(provider.Whitelist!, cartridge), Is.True, prototype);
                Assert.That(whitelists.IsValid(provider.Whitelist!, loader), Is.True, prototype);
                VerifyAmmunition(gun);
                entities.DeleteEntity(gun);
            }

            entities.DeleteEntity(cartridge);
            entities.DeleteEntity(loader);

            void VerifyAmmunition(EntityUid gun)
            {
                if (entities.TryGetComponent<ChamberMagazineAmmoProviderComponent>(gun, out var chamber))
                    entities.System<GunSystem>().SetBoltClosed(gun, chamber, true);

                var take = new TakeAmmoEvent(1, [], map.GridCoords, null);
                entities.EventBus.RaiseLocalEvent(gun, take);
                Assert.That(take.Ammo, Has.Count.EqualTo(1), entities.GetComponent<MetaDataComponent>(gun).EntityPrototype?.ID);
                Assert.That(take.Ammo[0].Entity, Is.Not.Null);
                var ammo = take.Ammo[0].Entity!.Value;
                Assert.That(entities.GetComponent<MetaDataComponent>(ammo).EntityPrototype?.ID,
                    Is.EqualTo("CartridgeMagnum"));
                entities.DeleteEntity(ammo);
            }
        });

        await pair.CleanReturnAsync();
    }
}
