using System.Linq;
using Content.Client._DeepLagoon.Speech;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Speech;
using Content.Shared.Speech;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private static readonly string[] SpeechBarkParameters = ["speed", "pitch", "variance", "pause"];
    private readonly ScrollContainer _speechBarkTab = new();
    private SpeechBarkVoicePrototype[] _speechBarkVoices = [];
    private OptionButton? _speechBarkVoice;
    private readonly List<(Slider Slider, Label Value)> _speechBarkSliders = new();
    private bool _updatingSpeechBarks;

    private void InitializeSpeechBarkEditor()
    {
        _speechBarkVoices = _prototypeManager.EnumeratePrototypes<SpeechBarkVoicePrototype>()
            .Where(voice => !voice.Hidden).OrderBy(voice => voice.Name).ToArray();

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(8),
            SeparationOverride = 6,
        };
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
            var slider = new Slider { HorizontalExpand = true, Rounded = true };
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
                StopSpeechBarkPreview();
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

    private ProtoId<SpeechBarkVoicePrototype>? GetDefaultSpeechBarkVoice()
    {
        if (Profile == null)
            return null;

        var species = _prototypeManager.Index(Profile.Species);
        var mob = _prototypeManager.Index(species.Prototype);
        return mob.TryGetComponent<SpeechComponent>(out var speech, _entManager.ComponentFactory)
            ? speech.BarkVoice
            : null;
    }

    private void PreviewSpeechBarks()
    {
        if (Profile != null)
            _entManager.System<SpeechBarkSystem>().Preview(Profile.SpeechBarks, GetDefaultSpeechBarkVoice());
    }

    private void StopSpeechBarkPreview() => _entManager.System<SpeechBarkSystem>().StopPreview();

    private void ResetSpeechBarks()
    {
        StopSpeechBarkPreview();
        ApplySpeechBarks(new SpeechBarkSettings());
    }

    private static string SpeechBarkValue(string field, int value) => field == "speed"
        ? value.ToString()
        : Loc.GetString("speech-bark-percent", ("value", value));
}
