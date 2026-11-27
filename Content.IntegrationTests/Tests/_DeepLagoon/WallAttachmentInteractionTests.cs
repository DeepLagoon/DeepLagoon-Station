using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._DeepLagoon.Construction;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class WallAttachmentInteractionTests : InteractionTest
{
    [TestCase("WallArmorAddonSolid")]
    [TestCase("WallArmorAddonSolidCornerL")]
    [TestCase("WallArmorAddonSolidCornerR")]
    [TestCase("GreebleElint1")]
    [TestCase("GreebleRadarpanel2")]
    [TestCase("GreebleAntenna1")]
    public async Task WrenchRemovesAndReattachesWallEquipment(string prototype)
    {
        await Server.WaitPost(() => SEntMan.SpawnEntity("WallSolid", SEntMan.GetCoordinates(TargetCoords)));
        await SpawnTarget(prototype);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<TransformComponent>(STarget!.Value).Anchored, Is.True);
            Assert.That(InteractSys.InRangeUnobstructed(SPlayer, STarget.Value), Is.True,
                "The supporting wall must not obstruct the real tool interaction");
        });
        await InteractUsing(Wrench, awaitDoAfters: false);
        await Server.WaitAssertion(() => Assert.That(ActiveDoAfters.Count(), Is.EqualTo(1)));
        await AwaitDoAfters();
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<TransformComponent>(STarget!.Value).Anchored, Is.False));
        await InteractUsing(Wrench);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<TransformComponent>(STarget!.Value).Anchored, Is.True);
            Assert.That(SEntMan.GetComponent<WallAttachmentComponent>(STarget.Value).Support, Is.Not.Null);
        });
    }
}
