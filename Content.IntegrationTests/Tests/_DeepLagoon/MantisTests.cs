using System.Collections.Generic;
using System.Linq;
using Content.Server._DeepLagoon.Mantis;
using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Access;
using Content.Shared.Clothing;
using Content.Shared.NPC.Systems;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Psionics;
using Content.Shared.Psionics.Components;
using Content.Shared.Roles;
using Content.Shared.StatusIcon;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class MantisTests
{
    [Test]
    public async Task MantisPrototypesLoad()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();

            foreach (var id in new[]
                     {
                         "PsiSpectrometer", "PsiSpectrometerConsole", "AntiPsychicKnife",
                         "InsulativeShield", "InsulativeShieldLight", "VeilbreakerGauntlet",
                         "MantisPDA", "TsfMantisPDA", "MantisIDCard", "TsfMantisIDCard",
                     })
            {
                Assert.That(prototypes.HasIndex<EntityPrototype>(id), Is.True, $"Missing prototype {id}");
            }

            foreach (var id in new[] { "TsfMantis", "Mantis" })
                Assert.That(prototypes.HasIndex<JobPrototype>(id), Is.True, $"Missing job {id}");

            foreach (var id in new[] { "JobTsfMantis", "JobMantis" })
                Assert.That(prototypes.HasIndex<PlayTimeTrackerPrototype>(id), Is.True, $"Missing play time tracker {id}");

            foreach (var id in new[] { "JobIconTsfMantis", "JobIconMantis" })
                Assert.That(prototypes.HasIndex<JobIconPrototype>(id), Is.True, $"Missing job icon {id}");

            Assert.That(prototypes.HasIndex<AccessLevelPrototype>("Mantis"), Is.True, "Missing Mantis access level");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SpectrometerClassifiesSignatures()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<PsiSpectrometerSystem>();
            var tags = entities.System<Content.Shared.Tag.TagSystem>();

            var device = entities.SpawnEntity("PsiSpectrometer", map.GridCoords);

            var psion = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.EnsureComponent<PsionicComponent>(psion);
            Assert.That(Classify(system, entities, device, psion), Is.EqualTo(NoosphericSignatureType.Sapient));

            tags.AddTag(psion, "NoosphericSignatureAnomalous");
            Assert.That(Classify(system, entities, device, psion), Is.EqualTo(NoosphericSignatureType.Anomalous));
            tags.RemoveTag(psion, "NoosphericSignatureAnomalous");

            entities.EnsureComponent<PsionicInsulationComponent>(psion);
            Assert.That(Classify(system, entities, device, psion), Is.Null);
            entities.RemoveComponent<PsionicInsulationComponent>(psion);

            var wisp = entities.SpawnEntity("MobGlimmerWisp", map.GridCoords);
            Assert.That(Classify(system, entities, device, wisp), Is.EqualTo(NoosphericSignatureType.Anomalous));

            var plain = entities.SpawnEntity("MobHuman", map.GridCoords);
            Assert.That(Classify(system, entities, device, plain), Is.Null);

            entities.DeleteEntity(plain);
            entities.DeleteEntity(wisp);
            entities.DeleteEntity(psion);
            entities.DeleteEntity(device);
        });

        await pair.CleanReturnAsync();
    }

    private static NoosphericSignatureType? Classify(PsiSpectrometerSystem system, IEntityManager entities,
        EntityUid device, EntityUid subject)
    {
        var component = entities.GetComponent<PsiSpectrometerComponent>(device);
        return system.TryReadSignature((device, component), subject, out var signature)
            ? signature.Type
            : null;
    }

    [Test]
    public async Task HunterModeTradesSafetyForReach()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<PsiSpectrometerSystem>();
            var factions = entities.System<NpcFactionSystem>();

            var holder = entities.SpawnEntity("MobHuman", map.GridCoords);
            var device = entities.SpawnEntity("PsiSpectrometer", map.GridCoords);
            var hands = entities.System<SharedHandsSystem>();
            var handsComp = entities.GetComponent<HandsComponent>(holder);
            Assert.That(hands.TryPickup(holder, device, checkActionBlocker: false, handsComp: handsComp), Is.True);

            var component = entities.GetComponent<PsiSpectrometerComponent>(device);
            var baseRange = system.EffectiveRange(component);

            system.SetHunterMode(device, true);
            Assert.That(component.HunterMode, Is.True);
            Assert.That(system.EffectiveRange(component), Is.GreaterThan(baseRange), "Hunter mode must extend the sweep");
            Assert.That(component.HunterFaction, Is.EqualTo("NoosphericHunter"),
                "Hunter mode must use its own faction, not the one psionics already grants");
            Assert.That(factions.IsMember(holder, component.HunterFaction), Is.True,
                "Hunter mode must make the operator a target for the Veil");
            Assert.That(factions.IsFactionHostile("GlimmerMonster", "NoosphericHunter"), Is.True,
                "Glimmer creatures must actually be hostile to a hunter");

            system.SetHunterMode(device, false);
            Assert.That(component.HunterMode, Is.False);
            Assert.That(system.EffectiveRange(component), Is.EqualTo(baseRange));
            Assert.That(factions.IsMember(holder, component.HunterFaction), Is.False,
                "Leaving hunter mode must not leave a stale faction behind");

            entities.DeleteEntity(device);
            entities.DeleteEntity(holder);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InsulativeShieldInsulatesWhileHeld()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var hands = entities.System<SharedHandsSystem>();

            var holder = entities.SpawnEntity("MobHuman", map.GridCoords);
            var handsComp = entities.GetComponent<HandsComponent>(holder);
            var shield = entities.SpawnEntity("InsulativeShield", map.GridCoords);

            Assert.That(hands.TryPickup(holder, shield, checkActionBlocker: false, handsComp: handsComp), Is.True);

            Assert.That(entities.TryGetComponent<PsionicInsulationComponent>(holder, out var insulation), Is.True,
                "Holding the shield must grant insulation");
            Assert.That(insulation!.Passthrough, Is.False, "The heavy shield must silence the bearer");

            Assert.That(hands.TryDrop(holder, shield, checkActionBlocker: false, handsComp: handsComp), Is.True);
            Assert.That(entities.HasComponent<PsionicInsulationComponent>(holder), Is.False,
                "Dropping the shield must remove the insulation");

            var light = entities.SpawnEntity("InsulativeShieldLight", map.GridCoords);
            Assert.That(hands.TryPickup(holder, light, checkActionBlocker: false, handsComp: handsComp), Is.True);
            Assert.That(entities.TryGetComponent<PsionicInsulationComponent>(holder, out var lightInsulation), Is.True);
            Assert.That(lightInsulation!.Passthrough, Is.True, "The light shield must allow self casting");

            entities.DeleteEntity(light);
            entities.DeleteEntity(shield);
            entities.DeleteEntity(holder);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VeilbreakerOnlyBitesTheVeil()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var weapon = entities.SpawnEntity("VeilbreakerGauntlet", map.GridCoords);
            var component = entities.GetComponent<AntiAnomalyWeaponComponent>(weapon);

            var wisp = entities.SpawnEntity("MobGlimmerWisp", map.GridCoords);
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);

            Assert.That(ModifiersFor(entities, weapon, wisp), Is.EqualTo(1), "A Veil entity must receive the bonus");
            Assert.That(ModifiersFor(entities, weapon, human), Is.EqualTo(0), "An ordinary target must not");

            Assert.That(component.Modifiers.Coefficients, Is.Not.Empty);
            entities.DeleteEntity(human);
            entities.DeleteEntity(wisp);
            entities.DeleteEntity(weapon);
        });

        await pair.CleanReturnAsync();
    }

    private static int ModifiersFor(IEntityManager entities, EntityUid weapon, EntityUid target)
    {
        var ev = new MeleeHitEvent(new List<EntityUid> { target }, EntityUid.Invalid, weapon,
            new Content.Shared.Damage.DamageSpecifier(), null);
        entities.EventBus.RaiseLocalEvent(weapon, ev);
        return ev.ModifiersList.Count;
    }

    [Test]
    public async Task RegistryStatusesMapToTheDocumentedNames()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That((byte) PsionicsStatus.None, Is.EqualTo(0));
            Assert.That((byte) PsionicsStatus.Suspected, Is.EqualTo(1), "Suspected is the unregistered status");
            Assert.That((byte) PsionicsStatus.Registered, Is.EqualTo(2), "Registered is the clean status");
            Assert.That((byte) PsionicsStatus.Abusing, Is.EqualTo(3), "Abusing is the hostile status");

            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            Assert.That(prototypes.HasIndex<EntityPrototype>("PsiRegistryCartridge"), Is.True,
                "The registry cartridge must exist for the Mantis to carry");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BothPostingsHaveRoleLoadouts()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();

            foreach (var (job, role) in new[] { ("Mantis", "JobMantis"), ("TsfMantis", "JobTsfMantis") })
            {
                Assert.That(LoadoutSystem.GetJobPrototype(job), Is.EqualTo(role));
                Assert.That(prototypes.HasIndex<RoleLoadoutPrototype>(role), Is.True,
                    $"{job} has no role loadout, so it cannot be customised and its kit has nowhere to go");

                var roleProto = prototypes.Index<RoleLoadoutPrototype>(role);
                Assert.That(roleProto.Groups, Is.Not.Empty, $"{role} has no groups");

                foreach (var groupId in roleProto.Groups)
                {
                    Assert.That(prototypes.HasIndex<LoadoutGroupPrototype>(groupId), Is.True,
                        $"{role} references missing group {groupId}");

                    var group = prototypes.Index<LoadoutGroupPrototype>(groupId);
                    Assert.That(group.Loadouts, Is.Not.Empty, $"{groupId} offers nothing");

                    foreach (var loadoutId in group.Loadouts.Concat(group.Fallbacks))
                        Assert.That(prototypes.HasIndex<LoadoutPrototype>(loadoutId), Is.True,
                            $"{groupId} references missing loadout {loadoutId}");
                }

                Assert.That(roleProto.Groups, Does.Contain("MantisBackpack"),
                    $"{role} must carry a backpack or its kit is dropped on the floor");
            }
        });
        await pair.CleanReturnAsync();
    }
}
