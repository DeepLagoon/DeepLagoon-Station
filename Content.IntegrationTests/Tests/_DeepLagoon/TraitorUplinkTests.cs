using System.Linq;
using System.Text.RegularExpressions;
using Content.Client.UserInterface.Systems.Chat;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.GameTicking.Rules;
using Content.Server.Mind;
using Content.Server.Traitor.Uplink;
using Content.Shared.Chat;
using Content.Shared.Dataset;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
using Content.Shared.Roles;
using Content.Shared.Roles.RoleCodeword;
using Content.Shared.Store.Components;
using Robust.Shared.GameObjects;
using Robust.Client.UserInterface;
using ServerChatSystem = Content.Server.Chat.Systems.ChatSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class TraitorUplinkTests
{
    private static readonly Regex RussianWord = new("^[а-яё]+$");

    [TestCase(true)]
    [TestCase(false)]
    public async Task TraitorReceivesRussianCodewordsAndOneHundredTelecrystals(bool hasPda)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Destructive = true,
            Connected = true,
            InLobby = true,
        });
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        string codeword = null;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var ruleUid = entities.System<GameTicker>().AddGameRule("Traitor");
            var rule = entities.GetComponent<TraitorRuleComponent>(ruleUid);
            var system = entities.System<TraitorRuleSystem>();
            Assert.That(rule.StartingBalance, Is.EqualTo((FixedPoint2) 100));
            Assert.That(rule.Codewords, Has.Length.EqualTo(rule.CodewordCount));
            Assert.That(rule.Codewords.Distinct().Count(), Is.EqualTo(rule.CodewordCount));

            var pool = pair.Server.ProtoMan.Index<DatasetPrototype>(rule.CodewordAdjectives).Values
                .Concat(pair.Server.ProtoMan.Index<DatasetPrototype>(rule.CodewordVerbs).Values).ToArray();
            Assert.That(pool.Distinct().Count(), Is.EqualTo(pool.Length));
            foreach (var word in pool)
            {
                Assert.That(RussianWord.IsMatch(word), Is.True, word);
                foreach (var channel in new[] { ChatChannel.Local, ChatChannel.Whisper, ChatChannel.Radio })
                {
                    var upper = word.ToUpperInvariant();
                    var message = new ChatMessage(channel, upper, $"{upper}!", NetEntity.Invalid, null);
                    Assert.That(SharedChatSystem.InjectTagAroundString(message, word, "color", "#cc3b3b"),
                        Is.EqualTo($"[color=#cc3b3b]{upper}[/color]!"), word);
                }
            }
            Assert.That(rule.Codewords.All(pool.Contains), Is.True);
            codeword = rule.Codewords[0].ToUpperInvariant();
            Assert.That(system.GenerateTraitorCodewords(new TraitorRuleComponent()).All(pool.Contains), Is.True);

            mob = entities.SpawnEntity("MobHuman", map.GridCoords);
            var minds = entities.System<MindSystem>();
            var mind = minds.GetOrCreateMind(pair.Server.PlayerMan.Sessions.Single().UserId);
            minds.TransferTo(mind.Owner, mob, mind: mind.Comp);
            entities.System<SharedRoleSystem>().MindAddRole(mind.Owner, "MindRoleTraitor", mind.Comp, silent: true);
            if (hasPda)
            {
                var pda = entities.SpawnEntity("PassengerPDA", map.GridCoords);
                Assert.That(entities.System<InventorySystem>().TryEquip(mob, pda, "id", force: true), Is.True);
                Assert.That(entities.System<UplinkSystem>().FindUplinkTarget(mob), Is.EqualTo(pda));
            }
            else
            {
                Assert.That(entities.System<UplinkSystem>().FindUplinkTarget(mob), Is.Null);
            }

            Assert.That(system.MakeTraitor(mob, rule), Is.True);
            var codewords = entities.GetComponent<RoleCodewordComponent>(mind.Owner).RoleCodewords["traitor"];
            Assert.That(codewords.Codewords, Is.EqualTo(rule.Codewords));
            var stores = entities.EntityQueryEnumerator<StoreComponent>();
            var ownedStores = 0;
            while (stores.MoveNext(out _, out var store))
            {
                if (store.AccountOwner != mind.Owner)
                    continue;
                ownedStores++;
                Assert.That(store.Balance[UplinkSystem.TelecrystalCurrencyPrototype], Is.EqualTo((FixedPoint2) 100));
            }
            Assert.That(ownedStores, Is.EqualTo(1));
        });
        await pair.RunTicksSync(10);
        foreach (var type in new[] { InGameICChatType.Speak, InGameICChatType.Whisper })
        {
            await pair.Server.WaitPost(() => pair.Server.EntMan.System<ServerChatSystem>()
                .TrySendInGameICMessage(mob, $"{codeword}!", type, false));
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                var chat = pair.Client.ResolveDependency<IUserInterfaceManager>().GetUIController<ChatUIController>();
                var channel = type == InGameICChatType.Speak ? ChatChannel.Local : ChatChannel.Whisper;
                var color = Robust.Shared.Maths.Color.FromHex("#cc3b3b").ToHex();
                Assert.That(chat.History.Any(entry => entry.Msg.Channel == channel
                    && entry.Msg.WrappedMessage.Contains($"[color={color}]{codeword}[/color]")), Is.True);
            });
        }
        await pair.CleanReturnAsync();
    }
}
