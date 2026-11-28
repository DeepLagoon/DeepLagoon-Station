using System.Linq;
using Content.Client.UserInterface.Systems.Chat;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Abilities.Psionics;
using Content.Server.Administration.Managers;
using Content.Server.Chat;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Bed.Sleep;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Drugs;
using Content.Shared.Psionics;
using Content.Shared.Psionics.Glimmer;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TelepathyDeliveryTests : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";

    [TestCase("ordinary", false)]
    [TestCase("sleeping", false)]
    [TestCase("hallucinating", false)]
    [TestCase("repeater", false)]
    [TestCase("ordinary", true)]
    [TestCase("sleeping", true)]
    [TestCase("hallucinating", true)]
    public async Task NonPsionsCannotReceiveThroughAnyDeliveryPath(string condition, bool administrator)
    {
        var chat = UiMan.GetUIController<ChatUIController>();
        await Client.WaitPost(() => chat.History.Clear());
        await Server.WaitAssertion(() =>
        {
            if (administrator)
                Server.ResolveDependency<IAdminManager>().PromoteHost(ServerSession);
            else
                Server.ResolveDependency<IAdminManager>().DeAdmin(ServerSession);
            if (condition == "sleeping")
                SEntMan.EnsureComponent<SleepingComponent>(SPlayer, out _);
            if (condition == "hallucinating")
                SEntMan.EnsureComponent<SeeingRainbowsComponent>(SPlayer, out _);
            if (condition == "repeater")
                SEntMan.EnsureComponent<TelepathicRepeaterComponent>(SPlayer, out _);
            Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.GlimmerEnabled, true);
            SEntMan.System<GlimmerSystem>().SetGlimmerOutput(999);
            var source = SEntMan.SpawnEntity("MobHuman", MapData.GridCoords);
            SEntMan.System<PsionicAbilitiesSystem>().InitializePsionicPower(source,
                ProtoMan.Index<PsionicPowerPrototype>("TelepathyPower"), false);
            var telepathy = SEntMan.System<TelepathicChatSystem>();
            Assert.That(telepathy.CanReceiveTelepathy(SPlayer), Is.False);
            for (var i = 0; i < 16; i++)
                telepathy.SendTelepathicChat(source, "private psionic message", false);
        });
        await RunTicks(10);
        await Client.WaitAssertion(() => Assert.That(chat.History.Count(x =>
            x.Msg.Channel is ChatChannel.Telepathic or ChatChannel.Local), Is.Zero));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PsionsReceiveOnceAndInsulationBlocksAllDelivery(bool administrator)
    {
        var chat = UiMan.GetUIController<ChatUIController>();
        await Client.WaitPost(() => chat.History.Clear());
        EntityUid source = default;
        await Server.WaitAssertion(() =>
        {
            if (administrator)
                Server.ResolveDependency<IAdminManager>().PromoteHost(ServerSession);
            else
                Server.ResolveDependency<IAdminManager>().DeAdmin(ServerSession);
            var powers = SEntMan.System<PsionicAbilitiesSystem>();
            var telepathy = ProtoMan.Index<PsionicPowerPrototype>("TelepathyPower");
            powers.InitializePsionicPower(SPlayer, telepathy, false);
            source = SEntMan.SpawnEntity("MobHuman", MapData.GridCoords);
            powers.InitializePsionicPower(source, telepathy, false);
            SEntMan.System<TelepathicChatSystem>().SendTelepathicChat(source, "allowed psionic message", false);
        });
        await RunTicks(10);
        await Client.WaitAssertion(() => Assert.That(chat.History.Count(x => x.Msg.Message == "allowed psionic message"), Is.EqualTo(1)));
        await Server.WaitAssertion(() =>
        {
            SEntMan.EnsureComponent<PsionicInsulationComponent>(SPlayer, out _);
            SEntMan.System<TelepathicChatSystem>().SendTelepathicChat(source, "blocked psionic message", false);
        });
        await RunTicks(10);
        await Client.WaitAssertion(() => Assert.That(chat.History.Count(x => x.Msg.Message == "blocked psionic message"), Is.Zero));
    }
}
