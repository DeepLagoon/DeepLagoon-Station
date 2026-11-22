using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.Speech;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Robust.Client.UserInterface;
using Robust.Server.GameStates;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterEditorAckTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task EditorActionsAndIconRequestsKeepAcknowledgingStates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var acknowledgements = new List<uint>();
        var states = pair.Server.ResolveDependency<IServerGameStateManager>();
        Action<ICommonSession, GameTick> observe = (_, tick) => acknowledgements.Add(tick.Value);
        EntityUid marker = default;
        NetEntity markerNet = default;
        await pair.Server.WaitPost(() =>
        {
            states.ClientAck += observe;
            var map = pair.Server.System<SharedMapSystem>().CreateMap();
            marker = pair.Server.EntMan.SpawnEntity(null, new Robust.Shared.Map.EntityCoordinates(map, Vector2.Zero));
            markerNet = pair.Server.EntMan.GetNetEntity(marker);
            pair.Server.System<PvsOverrideSystem>().AddGlobalOverride(marker);
        });
        try
        {
            await pair.RunTicksSync(20);
            var baseline = acknowledgements.Count;
            Assert.That(baseline, Is.GreaterThan(0), "The connected lobby client must receive and ACK real game states.");
            HumanoidProfileEditor editor = null;
            ConcurrentQueue<string> incoming = null;
            string icons = null;
            await pair.Client.WaitAssertion(() =>
            {
                var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
                var lobby = ui.GetUIController<LobbyUIController>();
                lobby.OpenCharacterEditor();
                editor = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", Private)!.GetValue(lobby)!;
                using var data = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
                icons = string.Join(',', data.RootElement.GetProperty("hairOptions").EnumerateArray()
                    .Skip(1).Take(80).Select(option => option.GetProperty("iconId").GetString()));
                Assert.That(icons, Is.Not.Empty);
                var panel = (TguiPanel) typeof(HumanoidProfileEditor).GetField("_profileWeb", Private)!.GetValue(editor)!;
                incoming = (ConcurrentQueue<string>) typeof(GameWebView).GetField("_incoming", Private)!.GetValue(panel.Web)!;
            });
            for (var i = 0; i < 100; i++)
            {
                var generation = i;
                await pair.Server.WaitPost(() => pair.Server.System<MetaDataSystem>().SetEntityName(marker, $"editor-probe-{generation}"));
                await pair.Client.WaitPost(() =>
                {
                    // Exercise the real bridge dispatch and deferred profile/preview updates.
                    // Headless tests do not render native Chromium or exercise UDP fragmentation.
                    incoming.Enqueue(Uri.EscapeDataString("act/hair-color") + "&" +
                        Uri.EscapeDataString(JsonSerializer.Serialize(new { value = generation % 2 == 0 ? "#123456" : "#654321" })));
                    if (generation % 20 == 0)
                        incoming.Enqueue(Uri.EscapeDataString("act/icons") + "&" +
                            Uri.EscapeDataString(JsonSerializer.Serialize(new { ids = icons })));
                });
                await pair.RunTicksSync(1);
            }
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                Assert.That(incoming, Is.Empty, "The bridge must drain its actions while networking keeps ticking.");
                Assert.That(pair.Client.EntMan.GetComponent<MetaDataComponent>(pair.Client.EntMan.GetEntity(markerNet)).EntityName,
                    Is.EqualTo("editor-probe-99"), "The client must actually apply the latest server state.");
                editor.ResetToDefault();
                pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
            });
            await pair.Server.WaitAssertion(() =>
            {
                var samples = acknowledgements.Skip(baseline).ToArray();
                Assert.That(samples.Length, Is.GreaterThanOrEqualTo(90));
                var maxGap = samples.Zip(samples.Skip(1), (previous, current) => current - previous).Max();
                Assert.That(maxGap, Is.LessThanOrEqualTo(2), "Opening/editing the character must not stop ACK progression.");
                TestContext.Out.WriteLine($"Editor: ACKs={samples.Length}, maximum ACK tick gap={maxGap}, applied=editor-probe-99.");
            });
        }
        finally
        {
            await pair.Server.WaitPost(() => states.ClientAck -= observe);
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EditorSpeechPreviewToleratesDisconnectAndReconnection()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        HumanoidProfileEditor editor = null;
        SpeechBarkSystem previousSpeech = null;
        string username = null;
        await pair.Server.WaitPost(() => username = pair.Server.PlayerMan.Sessions.Single().Name);
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            editor = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", Private)!.GetValue(lobby)!;
            Assert.That(pair.Client.EntMan.EntitySysManager.TryGetEntitySystem<SpeechBarkSystem>(out previousSpeech), Is.True);
            typeof(HumanoidProfileEditor).GetMethod("PreviewSpeechBarks", Private)!.Invoke(editor, null);
            Assert.That(typeof(SpeechBarkSystem).GetField("_preview", Private)!.GetValue(previousSpeech), Is.Not.Null,
                "Start an actual speech preview before tearing down the entity systems.");
            previousSpeech.FrameUpdate(0);
            pair.Client.ResolveDependency<IClientNetManager>().ClientDisconnect("Editor lifecycle regression test");
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(pair.Client.EntMan.EntitySysManager.TryGetEntitySystem<SpeechBarkSystem>(out _), Is.False);
            Assert.That(typeof(SpeechBarkSystem).GetField("_preview", Private)!.GetValue(previousSpeech), Is.Null,
                "Shutdown must release the old preview rather than leave playback behind.");
            // Late UI teardown must be harmless after the game's entity systems have gone away.
            Assert.DoesNotThrow(() => typeof(HumanoidProfileEditor).GetMethod("StopSpeechBarkPreview", Private)!.Invoke(editor, null));
            Assert.DoesNotThrow(() => typeof(HumanoidProfileEditor).GetMethod("PreviewSpeechBarks", Private)!.Invoke(editor, null));
        });
        pair.Client.SetConnectTarget(pair.Server);
        await pair.Client.WaitPost(() => pair.Client.ResolveDependency<IClientNetManager>().ClientConnect(null!, 0, username));
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(pair.Client.EntMan.EntitySysManager.TryGetEntitySystem<SpeechBarkSystem>(out var currentSpeech), Is.True);
            Assert.That(currentSpeech, Is.Not.SameAs(previousSpeech));
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            ui.GetUIController<LobbyUIController>().OpenCharacterEditor();
            var fresh = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", Private)!.GetValue(ui.GetUIController<LobbyUIController>())!;
            Assert.DoesNotThrow(() => typeof(HumanoidProfileEditor).GetMethod("PreviewSpeechBarks", Private)!.Invoke(fresh, null));
            Assert.DoesNotThrow(() => typeof(HumanoidProfileEditor).GetMethod("StopSpeechBarkPreview", Private)!.Invoke(fresh, null));
            fresh.ResetToDefault();
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }
}
