#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Server.Chat.Systems;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server._DeepLagoon.Currency;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.Speech;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Content.Shared.Speech;
using Robust.Shared.Audio.Components;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;
using Robust.Client.GameObjects;
using Content.Shared.Chat.TypingIndicator;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using ClientBarks = Content.Client._DeepLagoon.Speech.SpeechBarkSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SpeechBarkTests
{
    [Test]
    public async Task LocalSpeechAndWhispersSendOnlyToInRangeListeners()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid speaker = default;
        NetEntity netSpeaker = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var voices = pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<SpeechBarkVoicePrototype>()
                .ToDictionary(voice => voice.ID);
            Assert.That(voices, Has.Count.EqualTo(136));
            Assert.That(voices.Values.Count(voice => voice.Hidden), Is.EqualTo(3));
            Assert.That(voices["bullet"].MaxPitch, Is.EqualTo(1.6f));
            Assert.That(voices["bullet"].MinSpeed, Is.EqualTo(3));
            Assert.That(voices["toriel"].MaxPitch, Is.EqualTo(2.8f));
            Assert.That(voices["flowey2"].MinVariance, Is.Zero);
            Assert.That(voices["caw2"].AllowRandom, Is.True);
            var listener = entities.SpawnEntity("MobHuman", map.GridCoords);
            speaker = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1, 0)));
            netSpeaker = entities.GetNetEntity(speaker);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), listener);
        });
        await pair.RunTicksSync(10);

        List<SpeechBarkEvent> events = null!;
        await pair.Client.WaitAssertion(() =>
        {
            var recorder = pair.Client.EntMan.System<BarkRecorder>();
            recorder.Enabled = true;
            events = recorder.Events;
            events.Clear();
        });

        await Speak(InGameICChatType.Speak, "Капитан, вы слышите стук?");
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Source, Is.EqualTo(netSpeaker));
            Assert.That(events[0].Offsets, Is.EqualTo(new ushort[] { 0, 137, 274, 531, 694, 831, 968, 1131 }));
            Assert.That(events[0].Voice, Is.EqualTo(SpeechBarkVoicePrototype.Default));
            Assert.That(events[0].Pitches, Is.All.InRange(0.9f, 1.1f));
            Assert.That(events[0].Whisper, Is.False);
            AssertPlayingAtRange(ChatSystem.VoiceRange);
        });

        await Speak(InGameICChatType.Whisper, "Шлюз закрыт.");
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(events[1].Whisper, Is.True);
            Assert.That(events[1].Range, Is.EqualTo(ChatSystem.WhisperMuffledRange));
            AssertPlayingAtRange(ChatSystem.WhisperMuffledRange);
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.SpeechBarkVolume, 0f);
        });
        await pair.RunTicksSync(2);
        await pair.Client.WaitAssertion(() => Assert.That(PlayingBarks(), Is.Empty));

        await Speak(InGameICChatType.Speak, "Тихий жест.", "Sign");
        await Speak(InGameICChatType.Emote, "кивает");
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>()
            .SetCoordinates(speaker, map.GridCoords.Offset(new Vector2(7, 0))));
        await Speak(InGameICChatType.Whisper, "Далёкий шёпот.");
        await pair.Client.WaitAssertion(() => Assert.That(events, Has.Count.EqualTo(2)));

        await Speak(InGameICChatType.Speak, "Речь слышна.");
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(events, Has.Count.EqualTo(3));
            Assert.That(PlayingBarks(), Is.Empty);
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var voice = new SpeechBarkSettings
            {
                Voice = "chirp", PitchPercent = 80, Speed = 3, Pause = 150, VariancePercent = 20,
                BubbleStyle = SpeechBubbleStyle.Green,
            };
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithSpeechBarks(voice);
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(speaker, profile);
            Assert.That(entities.GetComponent<SpeechComponent>(speaker).BarkSettings, Is.EqualTo(voice));

            Assert.That(entities.GetComponent<SpeechComponent>(entities.SpawnEntity("MobSynth", map.GridCoords)).BarkVoice?.Id,
                Is.EqualTo("synth"));
            Assert.That(entities.GetComponent<SpeechComponent>(entities.SpawnEntity("MobMoth", map.GridCoords)).BarkVoice?.Id,
                Is.EqualTo("moff"));
            Assert.That(entities.GetComponent<SpeechComponent>(entities.SpawnEntity("MobCleanBot", map.GridCoords)).BarkVoice?.Id,
                Is.EqualTo("bottalk1"));
            Assert.That(entities.GetComponent<SpeechComponent>(entities.SpawnEntity("BorgChassisGeneric", map.GridCoords)).BarkVoice?.Id,
                Is.EqualTo("bottalk2"));
        });
        await Speak(InGameICChatType.Speak, "Мама, да?");
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(events, Has.Count.EqualTo(4));
            Assert.That(events[3].Offsets, Is.EqualTo(new ushort[] { 0, 102, 384 }));
            Assert.That(events[3].Voice.Id, Is.EqualTo("chirp"));
            Assert.That(events[3].Pitches, Is.All.InRange(0.7f, 0.9f));
            var entities = pair.Client.EntMan;
            Assert.That(entities.GetComponent<SpeechComponent>(entities.GetEntity(netSpeaker)).BubbleStyle,
                Is.EqualTo(SpeechBubbleStyle.Default));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var barks = entities.System<ClientBarks>();
            barks.Preview(new SpeechBarkSettings { Voice = "sans", PitchPercent = 80, VariancePercent = 0 }, null);
            barks.FrameUpdate(0f);
            var query = entities.EntityQueryEnumerator<AudioComponent>();
            AudioComponent? preview = null;
            while (query.MoveNext(out var audio))
            {
                if (audio.FileName == "/Audio/_DeepLagoon/Speech/BlueMoon/sans.ogg")
                    preview = audio;
            }
            Assert.That(preview, Is.Not.Null);
            Assert.That(preview!.Params.Pitch, Is.EqualTo(0.8f).Within(0.001f));
            barks.StopPreview();
        });
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.GetComponent<SpeechComponent>(speaker).SpeechSounds = "Pai");
        await Speak(InGameICChatType.Speak, "Синтезированный голос.");
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(events, Has.Count.EqualTo(5));
            Assert.That(events[4].Voice.Id, Is.EqualTo("chirp"));
        });

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var speech = entities.GetComponent<SpeechComponent>(speaker);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithSpeechBarks(speech.BarkSettings with { UseSpeciesSpeech = true });
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(speaker, profile);
            Assert.That(speech.SpeechSounds?.Id, Is.EqualTo("Pai"));
            var species = pair.Server.ResolveDependency<IPrototypeManager>().Index<EntityPrototype>("MobHuman");
            Assert.That(species.TryGetComponent<SpeechComponent>(out var speciesSpeech, entities.ComponentFactory), Is.True);
            Assert.That(speech.EffectiveSpeechSounds, Is.EqualTo(speciesSpeech!.SpeechSounds));
            speech.LastTimeSoundPlayed = TimeSpan.Zero;
        });
        await Speak(InGameICChatType.Speak, "Голос расы.");
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<SpeechComponent>(speaker).LastTimeSoundPlayed,
            Is.GreaterThan(TimeSpan.Zero)));
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(events, Has.Count.EqualTo(5));
            pair.Client.EntMan.System<BarkRecorder>().Enabled = false;
        });
        await pair.CleanReturnAsync();

        void AssertPlayingAtRange(int range)
        {
            var playing = PlayingBarks();
            Assert.That(playing, Is.Not.Empty);
            Assert.That(playing.Select(audio => audio.Params.MaxDistance), Is.All.EqualTo(range));
        }

        List<AudioComponent> PlayingBarks()
        {
            var entities = pair.Client.EntMan;
            entities.System<ClientBarks>().FrameUpdate(0f);
            var source = entities.GetEntity(netSpeaker);
            var audio = new List<AudioComponent>();
            var query = entities.EntityQueryEnumerator<AudioComponent, TransformComponent>();
            while (query.MoveNext(out var sound, out var xform))
            {
                var fileName = sound.FileName;
                if (xform.ParentUid == source && fileName.StartsWith("/Audio/_DeepLagoon/Speech/BlueMoon/", StringComparison.Ordinal))
                    audio.Add(sound);
            }

            return audio;
        }

        async Task Speak(InGameICChatType type, string text, string? languageId = null)
        {
            await pair.Server.WaitAssertion(() =>
            {
                var language = languageId == null
                    ? null
                    : pair.Server.ResolveDependency<IPrototypeManager>().Index<LanguagePrototype>(languageId);
                pair.Server.EntMan.System<ChatSystem>().TrySendInGameICMessage(
                    speaker, text, type, ChatTransmitRange.Normal, checkRadioPrefix: false, languageOverride: language);
            });
            await pair.RunTicksSync(6);
        }
    }

    [Test]
    public async Task CharacterEditorAppliesVoicePreferencesAndShowsLiveBubblePreview()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        HumanoidProfileEditor editor = null!;
        var draft = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithSpeechBarks(new SpeechBarkSettings
        {
            BubbleStyle = SpeechBubbleStyle.Green,
            TypingIndicator = "moth",
        });
        await pair.Server.WaitAssertion(() =>
        {
            var player = pair.Player!;
            SetTestPass(pair.Server.EntMan, player, 1);
            var preferences = pair.Server.ResolveDependency<IServerPreferencesManager>();
            var slot = preferences.GetPreferences(player.UserId).SelectedCharacterIndex;
            preferences.SetProfile(player.UserId, slot, draft, authoritative: false, persist: false).GetAwaiter().GetResult();
            var saved = (HumanoidCharacterProfile) preferences.GetPreferences(player.UserId).SelectedCharacter;
            Assert.That(saved.SpeechBarks.BubbleStyle, Is.EqualTo(SpeechBubbleStyle.Default));
            Assert.That(saved.SpeechBarks.TypingIndicator, Is.EqualTo("moth"));
        });
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            editor = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", fields)!.GetValue(lobby)!;
            var prefs = pair.Client.ResolveDependency<IClientPreferencesManager>().Preferences!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), prefs.SelectedCharacterIndex);
            using var state = JsonDocument.Parse(editor.CreateTguiProfileState("voice").ToString());
            var voiceTab = state.RootElement.GetProperty("tabs").EnumerateArray()
                .Single(tab => tab.GetProperty("mode").GetString() == "voice").GetProperty("id").GetInt32();
            Assert.That(editor.HandleTguiProfileAction("select-tab", JsonSerializer.Serialize(new { value = voiceTab })), Is.True);
            Assert.That(editor.HandleTguiProfileAction("speech-bark-use-species", "{\"value\":true}"), Is.True);
            Assert.That(editor.HandleTguiProfileAction("speech-bubble-select", "{\"value\":2}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("speech-bubble-preview", "{}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("speech-typing-select", "{\"value\":\"DeepLagoonLooc\"}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("speech-typing-select", "{\"value\":\"moth\"}"), Is.True);
            Assert.That(editor.HandleTguiProfileAction("speech-typing-preview", "{}"), Is.True);
            var typingEntity = (EntityUid) typeof(HumanoidProfileEditor).GetField("_typingIndicatorPreviewEntity", fields)!.GetValue(editor)!;
            var appearance = pair.Client.EntMan.System<AppearanceSystem>();
            Assert.That(appearance.TryGetData<string>(typingEntity, TypingIndicatorVisuals.CharacterPrototype, out var indicator), Is.True);
            Assert.That(indicator, Is.EqualTo("moth"));
        });
        await pair.Server.WaitAssertion(() => SetTestPass(pair.Server.EntMan, pair.Player!, 2));
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            Assert.That(editor.HandleTguiProfileAction("speech-bubble-select", "{\"value\":257}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("speech-bubble-select", "{\"value\":2}"), Is.True);
            Assert.That(editor.HandleTguiProfileAction("speech-bubble-preview", "{}"), Is.True);

            var previewField = typeof(HumanoidProfileEditor).GetField("_speechBubblePreview", fields)!;
            var window = (DefaultWindow) previewField.GetValue(editor)!;
            Assert.That(window.IsOpen, Is.True);
            var firstSample = window.Contents.Children.Single().Children.First();
            Assert.That(editor.HandleTguiProfileAction("speech-bubble-select", "{\"value\":4}"), Is.True);
            Assert.That(window.Contents.Children.Single().Children.First(), Is.Not.SameAs(firstSample));
            Assert.That(editor.Profile!.SpeechBarks.BubbleStyle, Is.EqualTo(SpeechBubbleStyle.Violet));
            Assert.That(editor.HandleTguiProfileAction("speech-bark-reset", "{}"), Is.True);
            Assert.That(editor.Profile!.SpeechBarks.UseSpeciesSpeech, Is.True);
            Assert.That(editor.Profile.SpeechBarks.BubbleStyle, Is.EqualTo(SpeechBubbleStyle.Violet));
            Assert.That(editor.Profile.SpeechBarks.TypingIndicator, Is.EqualTo("moth"));

            Assert.That(editor.HandleTguiProfileAction("select-tab", "{\"value\":0}"), Is.True);
            Assert.That(previewField.GetValue(editor), Is.Null);
            Assert.That(typeof(HumanoidProfileEditor).GetField("_typingIndicatorPreview", fields)!.GetValue(editor), Is.Null);
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
        });

        await pair.Server.WaitAssertion(() =>
        {
            var player = pair.Player!;
            var entities = pair.Server.EntMan;
            var preferences = pair.Server.ResolveDependency<IServerPreferencesManager>();
            var slot = preferences.GetPreferences(player.UserId).SelectedCharacterIndex;
            preferences.SetProfile(player.UserId, slot, draft, authoritative: false, persist: false).GetAwaiter().GetResult();
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(player.UserId).SelectedCharacter;
            Assert.That(profile.SpeechBarks.BubbleStyle, Is.EqualTo(SpeechBubbleStyle.Green));
            var character = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(character, profile);
            pair.Server.PlayerMan.SetAttachedEntity(player, character);
            var speech = entities.GetComponent<SpeechComponent>(character);
            Assert.That(speech.BubbleStyle, Is.EqualTo(SpeechBubbleStyle.Green));
            SetTestPass(entities, player, 1);
            Assert.That(speech.BubbleStyle, Is.EqualTo(SpeechBubbleStyle.Default));
        });
        await pair.CleanReturnAsync();
    }

    private static void SetTestPass(IEntityManager entities, ICommonSession player, int tier)
    {
        entities.System<LagoonEconomySystem>().Remember(player,
            new LagoonEconomySnapshot(0, new LagoonEconomyState { Revision = long.MaxValue, PassTier = tier, PassTicks = TimeSpan.FromHours(1).Ticks }));
        typeof(DiscordLinkSystem).GetMethod("PublishBoosts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(entities.System<DiscordLinkSystem>(), null);
    }

    private sealed class BarkRecorder : EntitySystem
    {
        public bool Enabled;
        public readonly List<SpeechBarkEvent> Events = new();

        public override void Initialize()
        {
            SubscribeNetworkEvent<SpeechBarkEvent>(ev =>
            {
                if (Enabled)
                    Events.Add(ev);
            });
        }
    }
}
