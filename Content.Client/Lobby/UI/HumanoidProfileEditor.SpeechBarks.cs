using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Speech;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Chat.UI;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.Speech;
using Content.Shared.Chat.TypingIndicator;
using Content.Shared.Speech;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Maths;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private static readonly string[] SpeechBarkParameters = ["speed", "pitch", "variance", "pause"];
    private readonly ScrollContainer _speechBarkTab = new();
    private SpeechBarkVoicePrototype[] _speechBarkVoices = [];
    private static readonly SpeechBubbleStyle[] SpeechBubbleStyles =
        [SpeechBubbleStyle.Default, SpeechBubbleStyle.Blue, SpeechBubbleStyle.Green, SpeechBubbleStyle.Amber, SpeechBubbleStyle.Violet];
    private OptionButton? _speechBarkVoice;
    private OptionButton? _speechBubbleStyle;
    private BoxContainer? _speechBubbleRow;
    private Label? _speechBubbleSupporter;
    private OptionButton? _typingIndicator;
    private TypingIndicatorPrototype[] _typingIndicators = [];
    private CheckBox? _legacySpeech;
    private DefaultWindow? _speechBubblePreview;
    private DefaultWindow? _typingIndicatorPreview;
    private EntityUid? _typingIndicatorPreviewEntity;
    private readonly List<(Slider Slider, Label Value)> _speechBarkSliders = new();
    private bool _updatingSpeechBarks;

    private void InitializeSpeechBarkEditor()
    {
        _speechBarkVoices = _prototypeManager.EnumeratePrototypes<SpeechBarkVoicePrototype>()
            .Where(voice => !voice.Hidden).OrderBy(voice => voice.Name).ToArray();
        _typingIndicators = _prototypeManager.EnumeratePrototypes<TypingIndicatorPrototype>()
            .Where(indicator => indicator.CharacterEditorName != null)
            .OrderBy(indicator => indicator.ID != SharedTypingIndicatorSystem.InitialIndicatorId.Id)
            .ThenBy(indicator => Loc.GetString(indicator.CharacterEditorName!.Value)).ToArray();

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(8),
            SeparationOverride = 6,
        };
        var typingRow = new BoxContainer { SeparationOverride = 8 };
        typingRow.AddChild(new Label { Text = Loc.GetString("speech-typing-indicator-style") });
        var typingStyle = new OptionButton { HorizontalExpand = true };
        _typingIndicator = typingStyle;
        for (var i = 0; i < _typingIndicators.Length; i++)
            typingStyle.AddItem(Loc.GetString(_typingIndicators[i].CharacterEditorName!.Value), i);
        typingStyle.OnItemSelected += args => ChangeTypingIndicator(_typingIndicators[args.Id].ID);
        typingRow.AddChild(typingStyle);
        var typingPreview = new Button { Text = Loc.GetString("speech-typing-indicator-preview") };
        typingPreview.OnPressed += _ => OpenTypingIndicatorPreview();
        typingRow.AddChild(typingPreview);
        content.AddChild(typingRow);

        var bubbleRow = new BoxContainer { SeparationOverride = 8 };
        _speechBubbleRow = bubbleRow;
        bubbleRow.AddChild(new Label { Text = Loc.GetString("speech-bubble-style") });
        var bubbleButton = new OptionButton { HorizontalExpand = true };
        _speechBubbleStyle = bubbleButton;
        foreach (var style in SpeechBubbleStyles)
            bubbleButton.AddItem(SpeechBubbleStyleName(style), (int) style);
        bubbleButton.OnItemSelected += args => ChangeSpeechBubbleStyle(args.Id);
        bubbleRow.AddChild(bubbleButton);
        var bubblePreview = new Button { Text = Loc.GetString("speech-bubble-preview") };
        bubblePreview.OnPressed += _ => OpenSpeechBubblePreview();
        bubbleRow.AddChild(bubblePreview);
        content.AddChild(bubbleRow);
        _speechBubbleSupporter = new Label { Text = Loc.GetString("speech-bubble-supporter") };
        content.AddChild(_speechBubbleSupporter);

        var legacySpeech = new CheckBox { Text = Loc.GetString("speech-bark-use-species") };
        _legacySpeech = legacySpeech;
        legacySpeech.OnToggled += args => ChangeSpeciesSpeech(args.Pressed);
        content.AddChild(legacySpeech);

        content.AddChild(new Label { Text = Loc.GetString("speech-bark-timbre") });
        var voiceButton = new OptionButton { HorizontalExpand = true };
        _speechBarkVoice = voiceButton;
        voiceButton.AddItem(Loc.GetString("speech-bark-timbre-automatic"), 0);
        for (var i = 0; i < _speechBarkVoices.Length; i++)
            voiceButton.AddItem(_speechBarkVoices[i].Name, i + 1);
        voiceButton.OnItemSelected += args => ChangeSpeechBarkVoice(args.Id == 0 ? "" : _speechBarkVoices[args.Id - 1].ID);
        content.AddChild(voiceButton);

        foreach (var field in SpeechBarkParameters)
        {
            var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 8 };
            row.AddChild(new Label { Text = Loc.GetString("speech-bark-" + field), MinWidth = 145 });
            var slider = new Slider { HorizontalExpand = true, MaxWidth = 280, Rounded = true };
            var value = new Label { MinWidth = 56 };
            row.AddChild(slider);
            row.AddChild(value);
            _speechBarkSliders.Add((slider, value));
            slider.OnValueChanged += control =>
            {
                if (!_updatingSpeechBarks)
                    ChangeSpeechBark(field, (int) control.Value);
            };
            content.AddChild(row);
        }

        var buttons = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 8 };
        var preview = new Button { Text = Loc.GetString("speech-bark-preview") };
        preview.OnPressed += _ => PreviewSpeechBarks();
        var random = new Button { Text = Loc.GetString("speech-bark-random") };
        random.OnPressed += _ => RandomizeSpeechBarkVoice();
        var reset = new Button { Text = Loc.GetString("speech-bark-reset") };
        reset.OnPressed += _ => ResetSpeechBarks();
        buttons.AddChild(preview);
        buttons.AddChild(random);
        buttons.AddChild(reset);
        content.AddChild(buttons);
        _speechBarkTab.AddChild(content);
        TabContainer.AddChild(_speechBarkTab);
        TabContainer.SetTabTitle(_speechBarkTab, Loc.GetString("dl-character-editor-tab-voice"));
        TabContainer.OnTabChanged += tab =>
        {
            if (TabContainer.GetChild(tab) != _speechBarkTab)
            {
                StopSpeechBarkPreview();
                CloseSpeechBubblePreview();
                CloseTypingIndicatorPreview();
            }
        };
        UpdateSpeechBarkControls();
    }

    private IEnumerable<TguiData> SpeechBarkOptions()
    {
        yield return new TguiData().String("id", "").String("name", Loc.GetString("speech-bark-timbre-automatic"));
        foreach (var voice in _speechBarkVoices)
            yield return new TguiData().String("id", voice.ID).String("name", voice.Name);
    }

    private TguiData BuildSpeechBarkData()
    {
        var settings = Profile?.SpeechBarks ?? new SpeechBarkSettings();
        var voice = settings.ResolveVoice(_prototypeManager, GetDefaultSpeechBarkVoice());
        settings = settings.ForVoice(voice);
        return new TguiData()
            .String("title", Loc.GetString("dl-character-editor-tab-voice"))
            .String("description", Loc.GetString("speech-bark-description"))
            .String("timbreLabel", Loc.GetString("speech-bark-timbre"))
            .String("search", Loc.GetString("speech-bark-search"))
            .String("preview", Loc.GetString("speech-bark-preview"))
            .String("random", Loc.GetString("speech-bark-random"))
            .String("reset", Loc.GetString("speech-bark-reset"))
            .String("typingLabel", Loc.GetString("speech-typing-indicator-style"))
            .String("typingPreview", Loc.GetString("speech-typing-indicator-preview"))
            .String("typingIndicator", settings.TypingIndicator)
            .Array("typingIndicators", _typingIndicators.Select(indicator => new TguiData()
                .String("id", indicator.ID).String("name", Loc.GetString(indicator.CharacterEditorName!.Value))))
            .Bool("canCustomizeBubble", CanCustomizeSpeechBubble())
            .String("bubbleSupporter", Loc.GetString("speech-bubble-supporter"))
            .String("bubbleLabel", Loc.GetString("speech-bubble-style"))
            .String("bubblePreview", Loc.GetString("speech-bubble-preview"))
            .Number("bubbleStyle", (int) settings.BubbleStyle)
            .Array("bubbleStyles", SpeechBubbleStyles.Select(style => new TguiData()
                .Number("id", (int) style).String("name", SpeechBubbleStyleName(style))))
            .String("useSpeciesLabel", Loc.GetString("speech-bark-use-species"))
            .Bool("useSpecies", settings.UseSpeciesSpeech)
            .String("voice", settings.Voice == voice.ID ? voice.ID : "")
            .Array("parameters", SpeechBarkParameters.Select(field =>
            {
                var (min, max, value) = SpeechBarkParameter(field, voice, settings);
                return new TguiData().String("id", field).String("label", Loc.GetString("speech-bark-" + field))
                    .Number("min", min).Number("max", max).Number("value", value)
                    .String("display", SpeechBarkValue(field, value));
            }));
    }

    private void UpdateSpeechBarkControls()
    {
        if (_speechBarkVoice == null)
            return;

        var settings = Profile?.SpeechBarks ?? new SpeechBarkSettings();
        var voice = settings.ResolveVoice(_prototypeManager, GetDefaultSpeechBarkVoice());
        settings = settings.ForVoice(voice);
        var selected = settings.Voice == voice.ID ? Array.FindIndex(_speechBarkVoices, entry => entry.ID == voice.ID) + 1 : 0;
        _speechBarkVoice.SelectId(selected);
        _speechBarkVoice.Disabled = settings.UseSpeciesSpeech;
        var bubbleAccess = CanCustomizeSpeechBubble();
        if (_speechBubbleRow != null) _speechBubbleRow.Visible = bubbleAccess;
        if (_speechBubbleSupporter != null) _speechBubbleSupporter.Visible = !bubbleAccess;
        if (bubbleAccess) _speechBubbleStyle?.SelectId((int) settings.BubbleStyle);
        else CloseSpeechBubblePreview();
        if (_typingIndicators.Length > 0)
            _typingIndicator?.SelectId(Math.Max(0, Array.FindIndex(_typingIndicators, item => item.ID == settings.TypingIndicator)));
        if (_legacySpeech != null)
            _legacySpeech.Pressed = settings.UseSpeciesSpeech;

        _updatingSpeechBarks = true;
        try
        {
            for (var i = 0; i < _speechBarkSliders.Count; i++)
            {
                var (slider, label) = _speechBarkSliders[i];
                var field = SpeechBarkParameters[i];
                var (min, max, value) = SpeechBarkParameter(field, voice, settings);
                slider.MinValue = 0;
                slider.MaxValue = max;
                slider.MinValue = min;
                slider.SetValueWithoutEvent(value);
                slider.Disabled = settings.UseSpeciesSpeech;
                label.Text = SpeechBarkValue(field, value);
            }
        }
        finally
        {
            _updatingSpeechBarks = false;
        }
    }

    private static (int Min, int Max, int Value) SpeechBarkParameter(string field, SpeechBarkVoicePrototype voice,
        SpeechBarkSettings settings) => field switch
    {
        "speed" => (voice.MinSpeed, voice.MaxSpeed, settings.Speed),
        "pitch" => (voice.MinPitchPercent, voice.MaxPitchPercent, settings.PitchPercent),
        "variance" => (voice.MinVariancePercent, voice.MaxVariancePercent, settings.VariancePercent),
        _ => (SpeechBarkSettings.MinPause, SpeechBarkSettings.MaxPause, settings.Pause),
    };

    private bool ChangeSpeechBarkVoice(string id)
    {
        if (Profile == null || (id.Length != 0 &&
            (!_prototypeManager.TryIndex<SpeechBarkVoicePrototype>(id, out var voice) || voice.Hidden)))
            return false;

        ApplySpeechBarks(Profile.SpeechBarks with { Voice = id });
        return true;
    }

    private void RandomizeSpeechBarkVoice()
    {
        var choices = _speechBarkVoices.Where(voice => voice.AllowRandom).ToArray();
        if (choices.Length > 0)
            ChangeSpeechBarkVoice(IoCManager.Resolve<IRobustRandom>().Pick(choices).ID);
    }

    private bool ChangeSpeechBark(string field, int value)
    {
        if (Profile == null)
            return false;

        var settings = Profile.SpeechBarks;
        var next = field switch
        {
            "speed" => settings with { Speed = value },
            "pitch" => settings with { PitchPercent = value },
            "variance" => settings with { VariancePercent = value },
            "pause" => settings with { Pause = value },
            _ => null,
        };
        if (next == null)
            return false;

        ApplySpeechBarks(next);
        return true;
    }

    private void ApplySpeechBarks(SpeechBarkSettings settings)
    {
        if (Profile == null)
            return;

        var voice = settings.ResolveVoice(_prototypeManager, GetDefaultSpeechBarkVoice());
        Profile = Profile.WithSpeechBarks(settings.ForVoice(voice));
        UpdateSpeechBarkControls();
        SetDirty();
    }

    private SpeechComponent? GetSpeciesSpeech()
    {
        if (Profile == null)
            return null;

        var species = _prototypeManager.Index(Profile.Species);
        var mob = _prototypeManager.Index(species.Prototype);
        return mob.TryGetComponent<SpeechComponent>(out var speech, _entManager.ComponentFactory)
            ? speech
            : null;
    }

    private ProtoId<SpeechBarkVoicePrototype>? GetDefaultSpeechBarkVoice() => GetSpeciesSpeech()?.BarkVoice;

    private void PreviewSpeechBarks()
    {
        if (Profile == null)
            return;

        var system = _entManager.System<SpeechBarkSystem>();
        var speciesSpeech = GetSpeciesSpeech();
        if (!Profile.SpeechBarks.UseSpeciesSpeech)
            system.Preview(Profile.SpeechBarks, speciesSpeech?.BarkVoice);
        else if (speciesSpeech?.SpeechSounds is { } sounds && _prototypeManager.TryIndex(sounds, out var sound))
            system.PreviewSpecies(sound.AskSound, speciesSpeech.AudioParams);
    }

    private void StopSpeechBarkPreview() => _entManager.System<SpeechBarkSystem>().StopPreview();

    private void ResetSpeechBarks()
    {
        StopSpeechBarkPreview();
        if (Profile == null)
            return;

        ApplySpeechBarks(new SpeechBarkSettings
        {
            UseSpeciesSpeech = Profile.SpeechBarks.UseSpeciesSpeech,
            BubbleStyle = Profile.SpeechBarks.BubbleStyle,
            TypingIndicator = Profile.SpeechBarks.TypingIndicator,
        });
    }

    private bool ChangeSpeciesSpeech(bool enabled)
    {
        if (Profile == null)
            return false;

        StopSpeechBarkPreview();
        ApplySpeechBarks(Profile.SpeechBarks with { UseSpeciesSpeech = enabled });
        return true;
    }

    private bool ChangeSpeechBubbleStyle(int style)
    {
        if (Profile == null || !CanCustomizeSpeechBubble() || style != (int) (SpeechBubbleStyle) style ||
            !SpeechBubbleStyles.Contains((SpeechBubbleStyle) style))
            return false;

        ApplySpeechBarks(Profile.SpeechBarks with { BubbleStyle = (SpeechBubbleStyle) style });
        UpdateSpeechBubblePreview();
        return true;
    }

    private static string SpeechBubbleStyleName(SpeechBubbleStyle style) =>
        Loc.GetString("speech-bubble-" + style.ToString().ToLowerInvariant());

    private bool CanCustomizeSpeechBubble() =>
        _entManager.System<SharedDiscordBoostSystem>().CanCustomizeSpeechBubble(_playerManager.LocalSession);

    private void OpenSpeechBubblePreview()
    {
        if (Profile == null || !CanCustomizeSpeechBubble())
            return;

        if (_speechBubblePreview is { } existing)
        {
            existing.MoveToFront();
            return;
        }

        var window = new DefaultWindow { Title = Loc.GetString("speech-bubble-preview"), MinWidth = 300 };
        _speechBubblePreview = window;
        window.OnClose += () => { _speechBubblePreview = null; window.Dispose(); };
        UpdateSpeechBubblePreview();
        window.OpenCentered();
    }

    private void UpdateSpeechBubblePreview()
    {
        if (_speechBubblePreview is not { } window || Profile == null)
            return;

        window.Contents.DisposeAllChildren();
        var samples = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 12, Margin = new Thickness(10) };
        samples.AddChild(FancyTextSpeechBubble.BuildPreview(Profile.Name, Loc.GetString("speech-bark-preview-phrase"), Profile.SpeechBarks.BubbleStyle));
        samples.AddChild(FancyTextSpeechBubble.BuildPreview(Profile.Name, Loc.GetString("speech-bubble-preview-whisper"), Profile.SpeechBarks.BubbleStyle, whisper: true));
        window.Contents.AddChild(samples);
    }

    private void CloseSpeechBubblePreview() => _speechBubblePreview?.Close();

    private bool ChangeTypingIndicator(string id)
    {
        if (Profile == null || !SharedTypingIndicatorSystem.IsCharacterIndicator(_prototypeManager, id))
            return false;

        ApplySpeechBarks(Profile.SpeechBarks with { TypingIndicator = id });
        if (_typingIndicatorPreviewEntity is { } entity)
            _entManager.System<SharedTypingIndicatorSystem>().ApplyCharacterIndicator(entity, id);
        return true;
    }

    private void OpenTypingIndicatorPreview()
    {
        if (Profile == null)
            return;
        if (_typingIndicatorPreview is { } existing)
        {
            existing.MoveToFront();
            return;
        }

        var entity = _controller.LoadProfileEntity(Profile, JobOverride, ShowClothes.Pressed);
        var typing = _entManager.System<SharedTypingIndicatorSystem>();
        typing.ApplyCharacterIndicator(entity, Profile.SpeechBarks.TypingIndicator);
        typing.SetTypingIndicatorState(entity, TypingIndicatorState.Typing);
        var portrait = new SpriteView
        {
            MinSize = new Vector2(195, 155), Scale = new Vector2(3), OverrideDirection = Direction.South,
        };
        portrait.SetEntity(entity);
        var window = new DefaultWindow { Title = Loc.GetString("speech-typing-indicator-preview"), MinWidth = 240 };
        window.Contents.AddChild(portrait);
        _typingIndicatorPreview = window;
        _typingIndicatorPreviewEntity = entity;
        window.OnClose += () =>
        {
            _typingIndicatorPreview = null;
            _typingIndicatorPreviewEntity = null;
            _entManager.DeleteEntity(entity);
            window.Dispose();
        };
        window.OpenCentered();
    }

    private void CloseTypingIndicatorPreview() => _typingIndicatorPreview?.Close();

    private static string SpeechBarkValue(string field, int value) => field == "speed"
        ? value.ToString()
        : Loc.GetString("speech-bark-percent", ("value", value));
}
