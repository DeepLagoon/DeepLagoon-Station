using System.Linq;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Weapons.Melee;
using Content.Shared.Wieldable.Components;
using Content.Shared._Shitmed.Targeting;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class LimbDamageTests
{
    [TestCase("MobHuman", "Heat")]
    [TestCase("MobVulpkanin", "Heat")]
    [TestCase("MobIPC", "Heat")]
    [TestCase("MobSkeletonPerson", "Heat")]
    [TestCase("MobHuman", "Blunt")]
    [TestCase("MobHuman", "Piercing")]
    [TestCase("MobHuman", "Cold")]
    [TestCase("MobHuman", "Caustic")]
    public async Task NonCuttingDamagePreservesAttachedAnatomy(string prototype, string damageType)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        EntityUid[] originalParts = null;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var body = entities.System<SharedBodySystem>();
            mob = entities.SpawnEntity(prototype, map.GridCoords);
            originalParts = body.GetBodyChildren(mob).Select(p => p.Id).ToArray();
            Assert.That(originalParts, Has.Length.GreaterThanOrEqualTo(10));
            entities.System<DamageableSystem>().TryChangeDamage(mob, Damage(damageType, 2000),
                targetPart: TargetBodyPart.All);
            Assert.That(entities.GetComponent<DamageableComponent>(mob).TotalDamage, Is.GreaterThan(FixedPoint2.Zero));
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.System<SharedBodySystem>().GetBodyChildren(mob).Select(p => p.Id),
                Is.EquivalentTo(originalParts));
            foreach (var part in originalParts)
            {
                Assert.That(entities.EntityExists(part), Is.True);
                Assert.That(entities.IsQueuedForDeletion(part), Is.False);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BurnedLimbNeedsItsOwnSevereCuttingDamage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var body = entities.System<SharedBodySystem>();
            var damage = entities.System<DamageableSystem>();
            var mob = entities.SpawnEntity("MobHuman", map.GridCoords);
            var arm = body.GetBodyChildrenOfType(mob, BodyPartType.Arm, symmetry: BodyPartSymmetry.Left).Single().Id;
            damage.TryChangeDamage(arm, Damage("Heat", 2000));
            damage.TryChangeDamage(arm, Damage("Blunt", 2000));
            damage.TryChangeDamage(arm, Damage("Piercing", 2000));
            damage.TryChangeDamage(arm, Damage("Slash", 20));
            Assert.That(entities.GetComponent<BodyPartComponent>(arm).Body, Is.EqualTo(mob),
                "Burns, blunt and piercing damage cannot substitute for accumulated slash damage.");
            damage.TryChangeDamage(arm, Damage("Slash", 200), canSever: false);
            damage.TryChangeDamage(arm, Damage("Slash", 1));
            Assert.That(entities.GetComponent<BodyPartComponent>(arm).Body, Is.EqualTo(mob),
                "A scratch must not finish off a badly injured limb.");
            damage.TryChangeDamage(arm, Damage("Slash", 20));
            Assert.That(entities.GetComponent<BodyPartComponent>(arm).Body, Is.Null);
            Assert.That(entities.IsQueuedForDeletion(arm), Is.False, "A severing hit must leave a recoverable limb.");
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Chainsaw")]
    [TestCase("HydroponicsToolScythe")]
    [TestCase("ButchCleaver")]
    [TestCase("EnergySword")]
    public async Task HeavyCuttingWeaponsCanStillSever(string weaponPrototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var body = entities.System<SharedBodySystem>();
            var damage = entities.System<DamageableSystem>();
            var mob = entities.SpawnEntity("MobHuman", map.GridCoords);
            var arm = body.GetBodyChildrenOfType(mob, BodyPartType.Arm, symmetry: BodyPartSymmetry.Left).Single().Id;
            var weapon = entities.SpawnEntity(weaponPrototype, map.GridCoords);
            var hit = entities.GetComponent<MeleeWeaponComponent>(weapon).Damage;
            if (entities.TryGetComponent(weapon, out IncreaseDamageOnWieldComponent wield))
                hit += wield.BonusDamage;
            if (entities.TryGetComponent(weapon, out ItemToggleMeleeWeaponComponent toggle))
                hit = toggle.ActivatedDamage;

            for (var i = 0; i < 40 && entities.GetComponent<BodyPartComponent>(arm).Body == mob; i++)
                damage.TryChangeDamage(mob, hit, targetPart: TargetBodyPart.LeftArm);

            Assert.That(entities.GetComponent<BodyPartComponent>(arm).Body, Is.Null, weaponPrototype);
            Assert.That(entities.IsQueuedForDeletion(arm), Is.False);
            Assert.That(body.GetBodyChildrenOfType(mob, BodyPartType.Arm, symmetry: BodyPartSymmetry.Right).Count(), Is.EqualTo(1));
            Assert.That(body.GetBodyChildrenOfType(mob, BodyPartType.Leg).Count(), Is.EqualTo(2));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(5, 0)]
    [TestCase(49, 0)]
    [TestCase(50, 1)]
    [TestCase(5000, 1)]
    public async Task BlastStrengthLimitsLimbLoss(int damagePerType, int expectedLostLimbs)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        EntityUid[] limbs = null;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var body = entities.System<SharedBodySystem>();
            var damage = entities.System<DamageableSystem>();
            mob = entities.SpawnEntity("MobHuman", map.GridCoords);
            limbs = body.GetBodyChildren(mob)
                .Where(p => p.Component.PartType is BodyPartType.Arm or BodyPartType.Leg).Select(p => p.Id).ToArray();
            Assert.That(limbs, Has.Length.EqualTo(4));
            // A weak blast must not exploit pre-existing burns to remove limbs.
            damage.TryChangeDamage(mob, Damage("Heat", 2000), canSever: false, targetPart: TargetBodyPart.All);
            var blast = Damage("Blunt", damagePerType) + Damage("Piercing", damagePerType) + Damage("Heat", damagePerType);
            // This is the same entry point/flags used by ExplosionSystem.ProcessEntity.
            damage.TryChangeDamage(mob, blast, ignoreResistances: true, ignoreGlobalModifiers: true,
                originFlag: DamageableSystem.DamageOriginFlag.Explosion);
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var body = entities.System<SharedBodySystem>();
            var remaining = body.GetBodyChildren(mob)
                .Where(p => p.Component.PartType is BodyPartType.Arm or BodyPartType.Leg).Select(p => p.Id).ToArray();
            Assert.That(remaining, Has.Length.EqualTo(4 - expectedLostLimbs));
            Assert.That(body.GetBodyChildrenOfType(mob, BodyPartType.Head).Count(), Is.EqualTo(1));
            foreach (var limb in limbs)
            {
                Assert.That(entities.EntityExists(limb), Is.True, "Even a strong blast must leave the severed limb recoverable.");
                Assert.That(entities.IsQueuedForDeletion(limb), Is.False);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(5, 0)]
    [TestCase(20, 1)]
    public async Task RealExplosionCannotRemoveEveryLimb(int intensity, int expectedLostLimbs)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            mob = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.System<DamageableSystem>().TryChangeDamage(mob, Damage("Heat", 2000),
                canSever: false, targetPart: TargetBodyPart.All);
            entities.System<Content.Server.Explosion.EntitySystems.ExplosionSystem>().QueueExplosion(
                mob, "Default", 1000, 3, intensity, tileBreakScale: 0, maxTileBreak: 0,
                canCreateVacuum: false, addLog: false);
        });
        await pair.RunTicksSync(120);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var body = entities.System<SharedBodySystem>();
            Assert.That(entities.GetComponent<DamageableComponent>(mob).Damage.DamageDict["Blunt"],
                Is.GreaterThan(FixedPoint2.Zero), "The explosion must actually hit the body.");
            Assert.That(body.GetBodyChildren(mob).Count(p => p.Component.PartType is BodyPartType.Arm or BodyPartType.Leg),
                Is.EqualTo(4 - expectedLostLimbs));
            Assert.That(body.GetBodyChildrenOfType(mob, BodyPartType.Head).Count(), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }
    private static DamageSpecifier Damage(string type, int amount) => new()
    {
        DamageDict = new() { [type] = FixedPoint2.New(amount) },
    };
}