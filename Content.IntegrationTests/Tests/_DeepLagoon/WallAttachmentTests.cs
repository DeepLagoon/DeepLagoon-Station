using System.Linq;
using Content.Server._DeepLagoon.Construction;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class WallAttachmentTests
{
    [TestCase("WallArmorAddonSolid", 30)]
    [TestCase("WallArmorAddonSolidCornerL", 30)]
    [TestCase("WallArmorAddonSolidCornerR", 30)]
    [TestCase("GreebleElint1", 40)]
    [TestCase("GreebleRadarpanel2", 40)]
    [TestCase("GreebleAntenna1", 40)]
    public async Task EquipmentProtectsItsWallAndFallsWhenTheWallIsRemoved(string prototype, int expectedDamage)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid wall = default, attachment = default;
        await pair.Server.WaitAssertion(() =>
        {
            wall = pair.Server.EntMan.SpawnEntity("WallConcreteIndestructible", map.GridCoords);
            attachment = pair.Server.EntMan.SpawnEntity(prototype, map.GridCoords);
            pair.Server.EntMan.System<SharedTransformSystem>().SetLocalRotation(attachment, Angle.FromDegrees(90));
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<WallAttachmentComponent>(attachment).Support, Is.EqualTo(wall));
            var damage = new DamageSpecifier(pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Blunt"), 50);
            entities.System<DamageableSystem>().TryChangeDamage(wall, damage);
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.Zero);
            Assert.That(entities.GetComponent<DamageableComponent>(attachment).TotalDamage.Float(), Is.EqualTo(expectedDamage));
            Assert.That(damage.DamageDict["Blunt"].Float(), Is.EqualTo(50), "A reused weapon damage spec must not be consumed by interception");
            entities.DeleteEntity(wall);
            Assert.That(entities.GetComponent<TransformComponent>(attachment).Anchored, Is.False);
            Assert.That(entities.GetComponent<WallAttachmentComponent>(attachment).Support, Is.Null);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplosionIsRoutedOnceAndExcessDamageReachesTheWall(bool wallFirst)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid wall = default, attachment = default;
        await pair.Server.WaitPost(() =>
        {
            wall = pair.Server.EntMan.SpawnEntity("WallConcreteIndestructible", map.GridCoords);
            attachment = pair.Server.EntMan.SpawnEntity("WallArmorAddonSolid", map.GridCoords);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var damage = entities.System<DamageableSystem>();
            var type = pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Blunt");
            void Blast(EntityUid uid) => damage.TryChangeDamage(uid, new DamageSpecifier(type, 100),
                ignoreResistances: true, ignoreGlobalModifiers: true, originFlag: DamageableSystem.DamageOriginFlag.Explosion);
            if (!wallFirst)
                Blast(attachment);
            Blast(wall);
            if (wallFirst)
                Blast(attachment);
            Assert.That(entities.GetComponent<DamageableComponent>(attachment).TotalDamage.Float(), Is.EqualTo(100));
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.Zero);
            damage.TryChangeDamage(wall, new DamageSpecifier(type, 5000), ignoreResistances: true,
                ignoreGlobalModifiers: true, originFlag: DamageableSystem.DamageOriginFlag.Explosion);
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.EqualTo(1100).Within(0.1),
                "The remaining 3900 durability absorbs part of the hit; only the excess hits the wall");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnanchoredAndBrokenEquipmentDoesNotProtectTheWall()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid wall = default, attachment = default;
        await pair.Server.WaitPost(() =>
        {
            wall = pair.Server.EntMan.SpawnEntity("WallConcreteIndestructible", map.GridCoords);
            attachment = pair.Server.EntMan.SpawnEntity("WallArmorAddonSolid", map.GridCoords);
            pair.Server.EntMan.SpawnEntity("WallArmorAddonSolidBroken", map.GridCoords);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedTransformSystem>().Unanchor(attachment);
            entities.System<DamageableSystem>().TryChangeDamage(wall,
                new DamageSpecifier(pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Blunt"), 50));
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(entities.GetComponent<DamageableComponent>(attachment).TotalDamage.Float(), Is.Zero);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RejectedAttachmentDamageDoesNotMakeTheWallInvulnerable()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid wall = default, attachment = default;
        await pair.Server.WaitPost(() =>
        {
            wall = pair.Server.EntMan.SpawnEntity("WallConcreteIndestructible", map.GridCoords);
            attachment = pair.Server.EntMan.SpawnEntity("WallArmorAddonSolid", map.GridCoords);
            pair.Server.EntMan.EnsureComponent<GodmodeComponent>(attachment, out _);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<DamageableSystem>().TryChangeDamage(wall,
                new DamageSpecifier(pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Blunt"), 50));
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(entities.GetComponent<DamageableComponent>(attachment).TotalDamage.Float(), Is.Zero);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullyResistedHitProtectsUnarmoredSupportUnlessTheAttachmentRejectsDamage(bool godmode)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid wall = default, attachment = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            wall = entities.SpawnEntity("WallConcreteIndestructible", map.GridCoords);
            attachment = entities.SpawnEntity("WallArmorAddonSolid", map.GridCoords);
            entities.System<DamageableSystem>().SetDamageModifierSetId(wall, null);
            if (godmode)
                entities.EnsureComponent<GodmodeComponent>(attachment, out _);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<WallAttachmentComponent>(attachment).Support, Is.EqualTo(wall));
            entities.System<DamageableSystem>().TryChangeDamage(wall,
                new DamageSpecifier(pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Blunt"), 10));
            Assert.That(entities.GetComponent<DamageableComponent>(attachment).TotalDamage.Float(), Is.Zero);
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.EqualTo(godmode ? 10 : 0),
                "Normal armor resistance absorbs the weak hit, but cancellation must pass it to the bare support");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NearlyBrokenArmorAppliesFlatReductionOnceAndPassesOnlyTheExcess()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid wall = default, attachment = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            wall = entities.SpawnEntity("WallConcreteIndestructible", map.GridCoords);
            attachment = entities.SpawnEntity("WallArmorAddonSolid", map.GridCoords);
            var damage = entities.System<DamageableSystem>();
            damage.SetDamageModifierSetId(wall, null);
            damage.TryChangeDamage(wall,
                new DamageSpecifier(pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Heat"), 10),
                ignoreResistances: true, ignoreGlobalModifiers: true);
            damage.TryChangeDamage(attachment,
                new DamageSpecifier(pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Blunt"), 3990),
                ignoreResistances: true, ignoreGlobalModifiers: true);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<WallAttachmentComponent>(attachment).Support, Is.EqualTo(wall));
            var type = pair.Server.ResolveDependency<IPrototypeManager>().Index<DamageTypePrototype>("Blunt");
            var hit = new DamageSpecifier(type, 50);
            hit.DamageDict["Heat"] = -5;
            entities.System<DamageableSystem>().TryChangeDamage(wall, hit);
            var wallDamage = entities.GetComponent<DamageableComponent>(wall).Damage;
            Assert.That(wallDamage.DamageDict["Blunt"].Float(), Is.EqualTo(100f / 3f).Within(.01f),
                "One third of the effective30 hit spends the remaining10 durability; two thirds reaches the wall");
            Assert.That(wallDamage.DamageDict["Heat"].Float(), Is.EqualTo(5),
                "Healing in a mixed damage request must still reach the wall unchanged");
            Assert.That(hit.DamageDict["Blunt"].Float(), Is.EqualTo(50));
            Assert.That(hit.DamageDict["Heat"].Float(), Is.EqualTo(-5));
            Assert.That(entities.IsQueuedForDeletion(attachment) || entities.Deleted(attachment), Is.True,
                "The armor must reach its destruction threshold rather than resist a scaled partial hit");
        });
        await pair.CleanReturnAsync();
    }
}
