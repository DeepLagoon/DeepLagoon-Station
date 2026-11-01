using System.Linq;
using System.Numerics;
using Content.Shared._EinsteinEngines.Silicon.DeadStartupButton;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SynthRevivalTests
{
    [Test]
    public async Task RebootRequiresRepairAndDisappearsAfterRevival()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        EntityUid synth = default;
        EntityUid user = default;
        string rebootText = null;

        await pair.Server.WaitAssertion(() =>
        {
            synth = entities.SpawnEntity("MobSynth", map.GridCoords);
            user = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(Vector2.UnitX));
            rebootText = Loc.GetString(entities.GetComponent<DeadStartupButtonComponent>(synth).VerbText);
            Assert.That(entities.System<SharedVerbSystem>().GetLocalVerbs(synth, user, typeof(AlternativeVerb))
                .Any(verb => verb.Text == rebootText), Is.False, "Living synths must not offer reboot");
            entities.System<DamageableSystem>().TryChangeDamage(synth,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = 165 } }, ignoreResistances: true);
            Assert.That(entities.System<MobStateSystem>().IsDead(synth), Is.True);
            var reboot = entities.System<SharedVerbSystem>().GetLocalVerbs(synth, user, typeof(AlternativeVerb))
                .Single(verb => verb.Text == rebootText);
            entities.System<SharedVerbSystem>().ExecuteVerb(reboot, user, synth);
        });

        await pair.RunTicksSync(90);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.System<MobStateSystem>().IsDead(synth), Is.True,
                "Reboot must not bypass the lethal damage threshold");
            entities.System<DamageableSystem>().TryChangeDamage(synth,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = -1 } }, ignoreResistances: true);
            Assert.That(entities.System<MobStateSystem>().IsDead(synth), Is.True,
                "Repair alone must not revive a synth");
            var reboot = entities.System<SharedVerbSystem>().GetLocalVerbs(synth, user, typeof(AlternativeVerb))
                .Single(verb => verb.Text == rebootText);
            entities.System<SharedVerbSystem>().ExecuteVerb(reboot, user, synth);
        });

        await pair.RunTicksSync(90);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.System<MobStateSystem>().IsAlive(synth), Is.True);
            Assert.That(entities.System<SharedVerbSystem>().GetLocalVerbs(synth, user, typeof(AlternativeVerb))
                .Any(verb => verb.Text == rebootText), Is.False);
            entities.DeleteEntity(synth);
            entities.DeleteEntity(user);
        });
        await pair.CleanReturnAsync();
    }
}
