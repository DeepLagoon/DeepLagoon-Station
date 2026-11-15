using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Systems.Chat;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Chat;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Content.Shared.Radio;
using Robust.Client.UserInterface;
using Robust.Server.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class GhostChatTests
{
    [Test]
    public async Task LocalChatRequiresSightAndRangeUnlessAnAdminGhostOptsIn()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        var entities = server.EntMan;
        var session = server.PlayerMan.Sessions.Single();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var admins = server.ResolveDependency<IAdminManager>();
        var chat = server.System<ChatSystem>();
        var transform = server.System<SharedTransformSystem>();
        var examine = server.System<ExamineSystemShared>();
        var near = map.GridCoords.Offset(new Vector2(1.75f, 0));
        var far = map.GridCoords.Offset(new Vector2(ChatSystem.VoiceRange + 5, 0));
        EntityUid listener = default;
        EntityUid speaker = default;
        EntityUid occluder = default;

        async Task CheckLocalChat(string marker, bool delivered)
        {
            await server.WaitPost(() =>
            {
                chat.TrySendInGameICMessage(speaker, $"{marker} speech", InGameICChatType.Speak, false);
                chat.TrySendInGameICMessage(speaker, $"{marker} emote", InGameICChatType.Emote, false);
                chat.TrySendInGameICMessage(speaker, $"{marker} whisper", InGameICChatType.Whisper, false);
            });
            await pair.RunTicksSync(5);
            await client.WaitAssertion(() =>
            {
                var history = client.ResolveDependency<IUserInterfaceManager>().GetUIController<ChatUIController>().History;
                foreach (var channel in new[] { ChatChannel.Local, ChatChannel.Emotes, ChatChannel.Whisper })
                {
                    Assert.That(history.Any(entry => entry.Msg.Channel == channel &&
                        entry.Msg.WrappedMessage.Contains(marker, System.StringComparison.OrdinalIgnoreCase)),
                        Is.EqualTo(delivered), $"{marker}: {channel}");
                }
            });
        }

        await server.WaitPost(() =>
        {
            listener = entities.SpawnEntity("MobObserver", near);
            speaker = entities.SpawnEntity("MobHuman", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(session, listener);
        });
        await pair.RunTicksSync(5);
        await CheckLocalChat("visible", true);

        await server.WaitPost(() =>
        {
            occluder = entities.SpawnEntity(null, map.GridCoords.Offset(new Vector2(0.875f, 0)));
            entities.AddComponent<OccluderComponent>(occluder);
        });
        await pair.RunTicksSync(5);
        await server.WaitAssertion(() => Assert.That(examine.InRangeUnOccluded(listener, speaker), Is.False));
        await CheckLocalChat("occluded", false);

        await server.WaitAssertion(() =>
        {
            entities.DeleteEntity(occluder);
            transform.SetCoordinates(listener, far);
            admins.PromoteHost(session);
            Assert.That(chat.TryToggleAdminGhostHearing(session, out _), Is.False);
            Assert.That(entities.HasComponent<GhostHearingComponent>(listener), Is.False);
        });
        await CheckLocalChat("distant", false);

        await server.WaitPost(() => server.System<RadioSystem>().SendRadioMessage(speaker, "radio marker", new ProtoId<RadioChannelPrototype>("CentCom"), speaker));
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var history = client.ResolveDependency<IUserInterfaceManager>().GetUIController<ChatUIController>().History;
            Assert.That(history.Any(entry => entry.Msg.Channel == ChatChannel.Radio && entry.Msg.WrappedMessage.Contains("radio marker")), Is.True);
        });

        await server.WaitPost(() =>
        {
            listener = entities.SpawnEntity("AdminObserver", far);
            server.PlayerMan.SetAttachedEntity(session, listener);
        });
        await pair.RunTicksSync(5);
        await CheckLocalChat("admindefault", false);

        await server.WaitAssertion(() =>
        {
            console.ExecuteCommand(session, "chat:toggle_all");
            Assert.That(entities.HasComponent<GhostHearingComponent>(listener), Is.True);
        });
        await CheckLocalChat("adminenabled", true);

        await server.WaitPost(() => admins.DeAdmin(session));
        await CheckLocalChat("adminrevoked", false);

        await server.WaitAssertion(() =>
        {
            admins.ReAdmin(session);
            console.ExecuteCommand(session, "chat:toggle_all");
            Assert.That(entities.HasComponent<GhostHearingComponent>(listener), Is.False);
        });
        await CheckLocalChat("admindisabled", false);
        await pair.CleanReturnAsync();
    }
}
