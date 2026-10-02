using System.Linq;
using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.InteractionPanel;

public sealed class InteractionPanelCustomEditor : BoxContainer
{
    public event Action<InteractionPanelCustomAction>? SaveRequested;
    public event Action<string>? DeleteRequested;
    public bool LibraryLoaded { get; private set; }
    private readonly BoxContainer _home = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 12 };
    private readonly BoxContainer _listPage = new() { Orientation = LayoutOrientation.Vertical, VerticalExpand = true, SeparationOverride = 10 };
    private readonly BoxContainer _list = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 8 };
    private readonly BoxContainer _wizard = new() { Orientation = LayoutOrientation.Vertical, VerticalExpand = true, SeparationOverride = 12 };
    private readonly BoxContainer[] _steps = Enumerable.Range(0, 5).Select(_ => new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 10 }).ToArray();
    private readonly Label _stepTitle = new();
    private readonly RichTextLabel _summary = new();
    private readonly Button _next = new() { Name = "CustomNext" };
    private readonly Button _back = new();
    private readonly Button _cancel = new();
    private readonly List<Button> _listButtons = new();
    private int _step;
    private readonly LineEdit _title = new() { Name = "CustomTitle" };
    private readonly LineEdit _template = new() { Name = "CustomTemplate" };
    private readonly OptionButton _category = new();
    private readonly OptionButton _body = new();
    private readonly CheckBox _self = new();
    private readonly CheckBox _other = new();
    private readonly CheckBox _humanoid = new();
    private readonly CheckBox _animal = new();
    private readonly LineEdit _color = new();
    private readonly OptionButton _soundCategory = new();
    private readonly OptionButton _sound = new();
    private readonly RichTextLabel _preview = new();
    private readonly RichTextLabel _status = new() { HorizontalExpand = true };
    private readonly Button _save = new() { Name = "CustomSave" };
    private readonly InteractionPanelSoundPrototype[] _sounds;
    private readonly string[] _soundCategories;
    private readonly List<string?> _soundIds = new();
    private InteractionPanelCustomAction[] _actions = Array.Empty<InteractionPanelCustomAction>();
    private string _id = "";
    private bool _waiting;

    public InteractionPanelCustomEditor()
    {
        Orientation = LayoutOrientation.Vertical;
        Margin = new Thickness(16);
        SeparationOverride = 8;
        _sounds = IoCManager.Resolve<IPrototypeManager>().EnumeratePrototypes<InteractionPanelSoundPrototype>()
            .OrderBy(s => Loc.GetString(s.Category)).ThenBy(s => Loc.GetString(s.Name)).ToArray();
        _soundCategories = _sounds.Select(s => s.Category).Distinct().ToArray();
        var listButton = new Button { Text = Loc.GetString("dl-interaction-panel-my-list"), MinHeight = 46 };
        var newButton = new Button { Text = Loc.GetString("dl-interaction-panel-add-new"), MinHeight = 46 };
        listButton.OnPressed += _ => ShowList();
        newButton.OnPressed += _ => BeginNew();
        _home.AddChild(listButton); _home.AddChild(newButton); AddChild(_home);
        var homeButton = new Button { Text = Loc.GetString("dl-interaction-panel-back-menu") };
        homeButton.OnPressed += _ => { if (!_waiting) ShowPage(_home); };
        _listPage.AddChild(homeButton);
        var listScroll = new ScrollContainer { VerticalExpand = true, HScrollEnabled = false };
        listScroll.AddChild(_list); _listPage.AddChild(listScroll); AddChild(_listPage);
        _wizard.AddChild(_stepTitle);
        var scroll = new ScrollContainer { Name = "EditorScroll", VerticalExpand = true, HorizontalExpand = true, HScrollEnabled = false };
        var form = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 8, HorizontalExpand = true };
        foreach (var step in _steps) form.AddChild(step);
        scroll.AddChild(form); _wizard.AddChild(scroll); AddChild(_wizard);
        var help = new RichTextLabel { Name = "EditorHelp", HorizontalExpand = true };
        help.SetMessage(Loc.GetString("dl-interaction-panel-step-text-help"));
        var helpCard = new PanelContainer { PanelOverride = new InteractionPanelRoundedStyle(InteractionPanelAppearance.EditorCard, 10, 12) };
        helpCard.AddChild(help); _steps[0].AddChild(helpCard);
        AddField(_steps[0], "dl-interaction-panel-editor-title", _title);
        AddField(_steps[0], "dl-interaction-panel-editor-template", _template);
        _title.PlaceHolder = Loc.GetString("dl-interaction-panel-editor-title-example");
        _template.PlaceHolder = Loc.GetString("dl-interaction-panel-editor-example");
        var tokens = new BoxContainer { SeparationOverride = 6 };
        foreach (var token in new[] { "$you", "$target" })
        {
            var button = new Button { Text = Loc.GetString("dl-interaction-panel-editor-insert", ("token", token)) };
            button.OnPressed += _ => { if (_waiting) return; _template.SetText(_template.Text + " " + token, true); };
            tokens.AddChild(button);
        }
        _steps[0].AddChild(tokens);
        var previewCard = new PanelContainer { PanelOverride = new InteractionPanelRoundedStyle(InteractionPanelAppearance.MessagePreview, 10, 12) };
        previewCard.AddChild(_preview); _steps[0].AddChild(previewCard);
        foreach (var category in Enum.GetValues<InteractionPanelCategory>())
            _category.AddItem(Loc.GetString($"dl-interaction-panel-category-{category.ToString().ToLowerInvariant()}"), (int) category);
        AddField(_steps[1], "dl-interaction-panel-editor-preference", _category);
        foreach (var body in InteractionPanelCustomRules.Bodies) _body.AddItem(Loc.GetString($"dl-interaction-panel-body-{body}"));
        AddField(_steps[1], "dl-interaction-panel-editor-region", _body);
        _self.Text = Loc.GetString("dl-interaction-panel-editor-self"); _other.Text = Loc.GetString("dl-interaction-panel-editor-other");
        _humanoid.Text = Loc.GetString("dl-interaction-panel-editor-humanoid"); _animal.Text = Loc.GetString("dl-interaction-panel-editor-animal");
        var flags = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
        flags.AddChild(_self); flags.AddChild(_other); flags.AddChild(_humanoid); flags.AddChild(_animal);
        _steps[2].AddChild(flags);
        AddField(_steps[3], "dl-interaction-panel-editor-color", _color);
        _soundCategory.AddItem(Loc.GetString("dl-interaction-panel-sounds-all"));
        foreach (var category in _soundCategories) _soundCategory.AddItem(Loc.GetString(category));
        AddField(_steps[3], "dl-interaction-panel-editor-sound-category", _soundCategory);
        AddField(_steps[3], "dl-interaction-panel-editor-sound", _sound);
        _category.OnItemSelected += args => { _category.SelectId(args.Id); Validate(); };
        _body.OnItemSelected += args => { _body.SelectId(args.Id); Validate(); };
        _soundCategory.OnItemSelected += args => { _soundCategory.SelectId(args.Id); FillSounds(); };
        _sound.OnItemSelected += args => { _sound.SelectId(args.Id); Validate(); };
        foreach (var edit in new[] { _title, _template, _color }) edit.OnTextChanged += _ => Validate();
        foreach (var flag in new[] { _self, _other, _humanoid, _animal }) flag.OnToggled += _ => Validate();
        foreach (var index in new[] { 1, 2, 3 })
        {
            var hint = new RichTextLabel();
            hint.SetMessage(Loc.GetString($"dl-interaction-panel-step-help-{index}"));
            _steps[index].AddChild(hint);
        }
        _steps[4].AddChild(_summary);
        var buttons = new BoxContainer { SeparationOverride = 8 };
        _cancel.Text = Loc.GetString("dl-interaction-panel-back-menu");
        _cancel.OnPressed += _ => { if (!_waiting) ShowPage(_home); };
        _back.Text = Loc.GetString("dl-interaction-panel-back");
        _back.OnPressed += _ => { if (!_waiting && _step > 0) { _step--; Validate(); } };
        _next.Text = Loc.GetString("dl-interaction-panel-next");
        _next.OnPressed += _ => { if (!_waiting && StepError() == null && _step < 4) { _step++; Validate(); } };
        _save.Text = Loc.GetString("dl-interaction-panel-editor-save");
        _save.OnPressed += _ => { var action = Read(); if (!LibraryLoaded || _waiting || !InteractionPanelCustomRules.Valid(action)) return; _waiting = true; Validate(); SaveRequested?.Invoke(action); };
        buttons.AddChild(_cancel); buttons.AddChild(_back); buttons.AddChild(_next); buttons.AddChild(_save);
        _wizard.AddChild(buttons); AddChild(_status);
        FillSounds(); Load(NewAction()); ShowPage(_home);
    }

    private void ShowPage(BoxContainer page)
    {
        _home.Visible = page == _home; _listPage.Visible = page == _listPage; _wizard.Visible = page == _wizard;
        _status.Visible = page != _home;
    }

    public void BeginNew()
    {
        if (_waiting) return;
        _step = 0; Load(NewAction()); ShowPage(_wizard);
    }

    private void ShowList()
    {
        if (_waiting) return;
        RefreshList(); ShowPage(_listPage);
        _status.SetMessage(LibraryLoaded ? "" : Loc.GetString("dl-interaction-panel-library-busy"));
    }

    private void RefreshList()
    {
        _list.RemoveAllChildren(); _listButtons.Clear();
        if (_actions.Length == 0)
        {
            var empty = new RichTextLabel(); empty.SetMessage(Loc.GetString("dl-interaction-panel-my-list-empty")); _list.AddChild(empty);
        }
        foreach (var action in _actions)
        {
            var card = new PanelContainer { PanelOverride = new InteractionPanelRoundedStyle(InteractionPanelAppearance.EditorCard, 10, 12) };
            var column = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 8 };
            var title = new RichTextLabel(); title.SetMessage(action.Title); column.AddChild(title);
            var buttons = new BoxContainer { SeparationOverride = 8 };
            var edit = new Button { Text = Loc.GetString("dl-interaction-panel-edit"), Disabled = _waiting };
            var delete = new Button { Text = Loc.GetString("dl-interaction-panel-editor-delete"), Disabled = _waiting };
            edit.OnPressed += _ => { if (_waiting) return; _step = 0; Load(action); ShowPage(_wizard); };
            delete.OnPressed += _ =>
            {
                if (_waiting) return;
                _waiting = true; Validate(); DeleteRequested?.Invoke(action.Id);
            };
            _listButtons.Add(edit); _listButtons.Add(delete);
            buttons.AddChild(edit); buttons.AddChild(delete); column.AddChild(buttons); card.AddChild(column); _list.AddChild(card);
        }
    }

    private string? StepError()
    {
        var action = Read();
        // Проверяем текущий шаг, подставляя корректные значения для ещё не заполненных шагов.
        return InteractionPanelCustomRules.ValidationError(_step switch
        {
            0 => new InteractionPanelCustomAction { Title = action.Title, Template = action.Template },
            1 => new InteractionPanelCustomAction { Title = "OK", Category = action.Category, Body = action.Body },
            2 => new InteractionPanelCustomAction { Title = "OK", Flags = action.Flags },
            3 => new InteractionPanelCustomAction { Title = "OK", ChatColor = action.ChatColor },
            _ => action,
        });
    }

    private static InteractionPanelCustomAction NewAction() => new() { Template = Loc.GetString("dl-interaction-panel-editor-example") };

    private static void AddField(BoxContainer form, string key, Robust.Client.UserInterface.Control control)
    {
        form.AddChild(new Label { Text = Loc.GetString(key) });
        control.HorizontalExpand = true;
        form.AddChild(control);
    }

    private void FillSounds()
    {
        _sound.Clear(); _soundIds.Clear();
        _sound.AddItem(Loc.GetString("dl-interaction-panel-sound-none"), 0); _soundIds.Add(null);
        foreach (var sound in _sounds.Where(s => _soundCategory.SelectedId == 0 || s.Category == _soundCategories[_soundCategory.SelectedId - 1]))
        {
            _sound.AddItem(Loc.GetString(sound.Category) + " — " + Loc.GetString(sound.Name), _soundIds.Count);
            _soundIds.Add(sound.ID);
        }
        _sound.SelectId(0);
    }

    private InteractionPanelCustomAction Read() => new()
    {
        Id = _id, Title = _title.Text.Trim(), Template = _template.Text.Trim(),
        Category = (InteractionPanelCategory) _category.SelectedId, Body = InteractionPanelCustomRules.Bodies[_body.SelectedId],
        Flags = (_self.Pressed ? InteractionPanelActionFlags.INTERACTION_SELF : 0) |
                (_other.Pressed ? InteractionPanelActionFlags.INTERACTION_OTHER : 0) |
                (_humanoid.Pressed ? InteractionPanelActionFlags.INTERACTION_HUMANOID : 0) |
                (_animal.Pressed ? InteractionPanelActionFlags.INTERACTION_NON_HUMANOID : 0),
        ChatColor = _color.Text.Trim(), Sound = _soundIds[_sound.SelectedId],
    };

    private void Load(InteractionPanelCustomAction action)
    {
        _id = action.Id; _title.Text = action.Title; _template.Text = action.Template; _color.Text = action.ChatColor;
        _category.SelectId((int) action.Category); _body.SelectId(Math.Max(0, Array.IndexOf(InteractionPanelCustomRules.Bodies, action.Body)));
        _self.Pressed = action.Flags.HasFlag(InteractionPanelActionFlags.INTERACTION_SELF);
        _other.Pressed = action.Flags.HasFlag(InteractionPanelActionFlags.INTERACTION_OTHER);
        _humanoid.Pressed = action.Flags.HasFlag(InteractionPanelActionFlags.INTERACTION_HUMANOID);
        _animal.Pressed = action.Flags.HasFlag(InteractionPanelActionFlags.INTERACTION_NON_HUMANOID);
        _soundCategory.SelectId(0); FillSounds();
        _sound.SelectId(Math.Max(0, _soundIds.IndexOf(action.Sound)));
        Validate();
    }

    private void Validate()
    {
        if (_soundIds.Count == 0) return;
        var action = Read();
        foreach (var edit in new[] { _title, _template, _color }) edit.Editable = !_waiting;
        foreach (var option in new[] { _category, _body, _soundCategory, _sound }) option.Disabled = _waiting;
        foreach (var flag in new[] { _self, _other, _humanoid, _animal }) flag.Disabled = _waiting;
        var error = InteractionPanelCustomRules.ValidationError(action);
        _save.Disabled = !LibraryLoaded || _waiting || error != null;
        _save.ToolTip = Loc.GetString(_waiting || !LibraryLoaded ? "dl-interaction-panel-library-busy" : error ?? "dl-interaction-panel-editor-valid");
        foreach (var button in _listButtons) button.Disabled = _waiting;
        _back.Disabled = _waiting || _step == 0;
        _cancel.Disabled = _waiting;
        _next.Visible = _step < 4; _save.Visible = _step == 4;
        _next.Disabled = !LibraryLoaded || _waiting || StepError() != null;
        _next.ToolTip = Loc.GetString(_waiting || !LibraryLoaded ? "dl-interaction-panel-library-busy" : StepError() ?? "dl-interaction-panel-next");
        for (var i = 0; i < _steps.Length; i++) _steps[i].Visible = i == _step;
        _stepTitle.Text = Loc.GetString("dl-interaction-panel-step-title", ("number", _step + 1), ("title", Loc.GetString($"dl-interaction-panel-step-{_step}")));
        _summary.SetMessage(Loc.GetString("dl-interaction-panel-review", ("name", action.Title),
            ("text", InteractionPanelCustomRules.Render(action.Template, Loc.GetString("dl-interaction-panel-editor-you"), Loc.GetString("dl-interaction-panel-editor-target"))),
            ("preference", Loc.GetString($"dl-interaction-panel-category-{action.Category.ToString().ToLowerInvariant()}")),
            ("body", Loc.GetString($"dl-interaction-panel-body-{action.Body}")),
            ("participants", string.Join(", ", new[] { _self, _other, _humanoid, _animal }.Where(f => f.Pressed).Select(f => f.Text))), ("color", action.ChatColor),
            ("sound", action.Sound == null ? Loc.GetString("dl-interaction-panel-sound-none") : Loc.GetString(_sounds.First(s => s.ID == action.Sound).Name))));
        var text = InteractionPanelCustomRules.Render(action.Template, Loc.GetString("dl-interaction-panel-editor-you"), Loc.GetString("dl-interaction-panel-editor-target"));
        _preview.SetMessage(FormattedMessage.EscapeText(text));
        if (Color.TryFromHex(action.ChatColor) is { } color) _preview.Modulate = color;
        _status.SetMessage(_step == 4 ? _save.ToolTip! : _next.Disabled ? _next.ToolTip! : "");
    }

    public void SetLibrary(InteractionPanelLibraryEvent data)
    {
        LibraryLoaded = true;
        _actions = data.Actions;
        _waiting = false;
        RefreshList();
        if (data.Message == "dl-interaction-panel-library-saved") ShowList();
        Validate();
        if (data.Message != "") _status.SetMessage(Loc.GetString(data.Message));
    }
}




