using System.Collections.Generic;
using System.Linq;
using Content.Client._DeepLagoon.Tattoos;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Sprite;

[TestFixture]
public sealed class ProstheticAppearanceTest
{
    [TestCase("Resomi")]
    [TestCase("Vox")]
    [TestCase("Vulpkanin")]
    [TestCase("Synth")]
    public async Task InstalledLimbsMatchRecipientInEveryDirection(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        NetEntity netMob = default;
        EntityUid originalHand = default;
        EntityUid prostheticHand = default;

        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            mob = em.SpawnEntity("Mob" + species, map.GridCoords);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
            if (species == "Vulpkanin")
                profile = profile.WithCharacterAppearance(new HumanoidCharacterAppearance(profile.Appearance) { PlantigradeLegs = true });
            if (species == "Synth")
            {
                var appearance = profile.Appearance.WithMarkings([new Marking("SynthFeroxiLLeg", 1), new Marking("SynthFeroxiLFoot", 1)]);
                profile = profile.WithCharacterAppearance(appearance);
            }
            em.System<SharedHumanoidAppearanceSystem>().LoadProfile(mob, profile);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), mob);
            netMob = em.GetNetEntity(mob);
        });

        RSI.State GetState(HumanoidVisualLayers part)
        {
            var em = pair.Client.EntMan;
            var uid = em.GetEntity(netMob);
            var sprite = em.GetComponent<SpriteComponent>(uid);
            var humanoid = em.GetComponent<HumanoidAppearanceComponent>(uid);
            var prototypes = pair.Client.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>();
            Assert.That(TattooBodyPreview.TrySource(humanoid, sprite, prototypes, part, out var index), Is.True, part.ToString());
            Assert.That(sprite[index].Visible, Is.True, part.ToString());
            return ((SpriteComponent.Layer) sprite[index]).ActualState!;
        }

        var parts = new[]
        {
            HumanoidVisualLayers.LArm,
            HumanoidVisualLayers.LHand,
            HumanoidVisualLayers.RHand,
            HumanoidVisualLayers.LLeg,
            HumanoidVisualLayers.LFoot,
        };
        var native = new Dictionary<HumanoidVisualLayers, RSI.State>();
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            foreach (var part in parts)
                native.Add(part, GetState(part));
            if (species == "Synth")
                Assert.That(native[HumanoidVisualLayers.LLeg].RSI.Path.ToString(), Does.Contain("/Feroxi/"));
        });

        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var body = em.System<SharedBodySystem>();
            var torso = body.GetRootPartOrNull(mob)!.Value.Entity;
            var rightArm = body.GetBodyChildrenOfType(mob, BodyPartType.Arm, symmetry: BodyPartSymmetry.Right).Single().Id;

            (EntityUid Original, EntityUid Replacement) Replace(EntityUid parent, string prototype, BodyPartType type,
                BodyPartSymmetry symmetry)
            {
                var (original, part) = body.GetBodyChildrenOfType(mob, type, symmetry: symmetry).Single();
                var slot = part.ParentSlot!.Value.Id;
                Assert.That(body.DetachPart(parent, slot, original), Is.True);
                var replacement = em.SpawnEntity(prototype, map.GridCoords);
                Assert.That(body.AttachPart(parent, slot, replacement), Is.True);
                return (original, replacement);
            }

            Replace(torso, "JawsOfLifeLeftArm", BodyPartType.Arm, BodyPartSymmetry.Left);
            Replace(torso, "SpeedLeftLeg", BodyPartType.Leg, BodyPartSymmetry.Left);
            (originalHand, prostheticHand) = Replace(rightArm, "DexRightHand", BodyPartType.Hand, BodyPartSymmetry.Right);
            if (species == "Synth")
            {
                var humanoid = em.GetComponent<HumanoidAppearanceComponent>(mob);
                Assert.That(humanoid.MarkingSet.Markings[MarkingCategories.Legs].Select(marking => marking.MarkingId),
                    Does.Not.Contain("SynthFeroxiLLeg").And.Contain("SynthHumanRLeg"));
            }
        });

        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            foreach (var part in parts)
            {
                var installed = GetState(part);
                Assert.That(installed.RSI.Path.ToString(), Does.Contain("_Generated/Prosthetics"), part.ToString());
                for (var direction = 0; direction < 4; direction++)
                {
                    using var expected = TattooSpriteFrames.Read(native[part], direction);
                    using var actual = TattooSpriteFrames.Read(installed, direction);
                    Assert.That(actual.Size, Is.EqualTo(expected.Size));
                    for (var y = 0; y < expected.Height; y++)
                    for (var x = 0; x < expected.Width; x++)
                        Assert.That(actual[x, y].A, Is.EqualTo(expected[x, y].A),
                            $"{species} {part}, direction {direction}, pixel ({x}, {y})");
                }
            }
        });

        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var body = em.System<SharedBodySystem>();
            var arm = body.GetBodyChildrenOfType(mob, BodyPartType.Arm, symmetry: BodyPartSymmetry.Right).Single().Id;
            var slot = em.GetComponent<BodyPartComponent>(prostheticHand).ParentSlot!.Value.Id;
            Assert.That(body.DetachPart(arm, slot, prostheticHand), Is.True);
            Assert.That(body.AttachPart(arm, slot, originalHand), Is.True);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
            Assert.That(GetState(HumanoidVisualLayers.RHand), Is.SameAs(native[HumanoidVisualLayers.RHand])));
        await pair.CleanReturnAsync();
    }
}
