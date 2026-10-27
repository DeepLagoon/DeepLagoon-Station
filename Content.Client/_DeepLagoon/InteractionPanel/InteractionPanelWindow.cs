using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby.UI;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.LustWish;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.InteractionPanel;

/// <summary>Compact TGUI with the live, shader-aware character sprite above its reserved thumbnail.</summary>
public sealed class InteractionPanelWindow : BaseWindow
{
    private readonly TguiPanel _panel = new(inheritChatAppearance: false, forceTgui: true);
    private readonly InteractionPanelPanelEvent _data;
    private readonly HashSet<string> _available;
    private readonly HashSet<string> _favorites;
    private readonly HashSet<string> _repeating = new();
    private readonly InteractionPanelActionDefinition[] _standard;
    private InteractionPanelActionDefinition[] _definitions;
    private InteractionPanelCustomAction[] _custom = Array.Empty<InteractionPanelCustomAction>();
    private readonly TguiData _labels;
    private readonly TguiData[] _sounds;
    private readonly HashSet<string> _soundIds;
    private static TguiSpriteImages _images => TguiSpriteImages.Shared;
    private readonly TguiData _dollParts;
    private string _dollTexture = "";
    private bool _libraryLoaded;
    private bool _waiting;
    private int _libraryRevision;
    private bool _librarySaved;
    private string _status = Loc.GetString("dl-interaction-panel-ready");
    private string _libraryStatus = "";
    public NetEntity Target => _data.Target;
    public float Interval { get; private set; }
    public IEnumerable<string> Favorites => _favorites;
    public event Action<string>? ActionRequested;
    public event Action<string, bool, float>? RepeatRequested;
    public event Action? PreferencesChanged;
    public event Action<InteractionPanelCustomAction>? SaveRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string, int>? BodyVisibilityRequested;
    public event Action<string, string, string>? GearRequested;
    private LustWishGearStateEvent? _gear;
    public void SetGearState(LustWishGearStateEvent state)
    {
        _gear = state;
        Publish(gearOnly: true);
    }
    private BodyFeature[] _bodyFeatures = [];
    private readonly IEntityManager _entities = IoCManager.Resolve<IEntityManager>();
    private readonly IPlayerManager _players = IoCManager.Resolve<IPlayerManager>();
    private float _arousalCurrent;
    private float _arousalMaximum = 100;

    private bool UpdateArousal()
    {
        var current = 0f;
        var maximum = 100f;
        if (_entities.TryGetComponent(_players.LocalEntity, out InteractionPanelArousalComponent? arousal))
        {
            maximum = float.IsFinite(arousal.Maximum) && arousal.Maximum > 0 ? arousal.Maximum : 100;
            current = float.IsFinite(arousal.Current) ? Math.Clamp(arousal.Current, 0, maximum) : 0;
        }
        if (_arousalCurrent == current && _arousalMaximum == maximum) return false;
        _arousalCurrent = current;
        _arousalMaximum = maximum;
        return true;
    }

    public void SetAvailability(InteractionPanelAvailabilityEvent state)
    {
        if (state.Target != Target) return;
        _data.UserSexTraits = state.UserSexTraits;
        _data.TargetSexTraits = state.TargetSexTraits;
        _available.RemoveWhere(id => !id.StartsWith("custom:", StringComparison.Ordinal));
        _available.UnionWith(state.Actions);
        Publish();
    }

    public void SetBodyState(InteractionPanelBodyStateEvent state)
    {
        _bodyFeatures = state.Features;
        Publish();
    }

    public InteractionPanelWindow(InteractionPanelPanelEvent data, EntityUid? target, IEnumerable<string> favorites, float interval)
    {
        _data = data;
        _available = data.Actions.ToHashSet();
        _favorites = favorites.ToHashSet();
        Interval = InteractionPanelActions.ClampInterval(interval);
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        _definitions = _standard = InteractionPanelActionCatalog.Load(prototypes);
        _sounds = prototypes.EnumeratePrototypes<InteractionPanelSoundPrototype>()
            .OrderBy(s => Loc.GetString(s.Category)).ThenBy(s => Loc.GetString(s.Name))
            .Select(s => new TguiData().String("id", s.ID).String("name", Loc.GetString(s.Name))
                .String("category", Loc.GetString(s.Category))).ToArray();
        _soundIds = prototypes.EnumeratePrototypes<InteractionPanelSoundPrototype>().Select(s => s.ID).ToHashSet();
        _labels = new TguiData();
        foreach (var key in LabelKeys)
            _labels.String(key, Loc.GetString("dl-interaction-panel-" + key));
        _dollParts = new TguiData();
        foreach (var part in new[] { "head", "torso", "groin", "rightarm", "leftarm", "righthand", "lefthand", "rightleg", "leftleg", "rightfoot", "leftfoot" })
            _dollParts.String(part, _images.Frame(new SpriteSpecifier.Texture(new ResPath("_Shitmed/Interface/Targeting/Doll/" + part + ".png"))));
        UpdateDollTexture();
        MouseFilter = MouseFilterMode.Stop;
        MinSize = new Vector2(360, 420);
        SetSize = new Vector2(420, 560);
        var preview = new InteractionPanelSpriteView
        {
            Stretch = SpriteView.StretchMode.Fill, OverrideDirection = Direction.South,
            MouseFilter = MouseFilterMode.Ignore,
        };
        preview.SetEntity(target);
        var host = new InteractionPanelHost(_panel, preview) { Margin = new Thickness(8) };
        AddChild(host);
        _panel.NativeModeChanged += () => preview.Visible = !_panel.UsingNative;
        _panel.OnClose += Close;
        _panel.OnAction += OnAction;
        UpdateArousal();
        Publish();
    }

    private static string ActionName(InteractionPanelActionDefinition action) => action.Title ?? Loc.GetString("dl-interaction-panel-action-" + action.Id);

    public void SetStatus(InteractionPanelStatusEvent status)
    {
        _repeating.Clear();
        _repeating.UnionWith(status.Repeating);
        _status = Loc.GetString(status.Message);
        if (_repeating.Count > 0)
            _status += " — " + string.Join(", ", _repeating.Select(id => _definitions.FirstOrDefault(a => a.Id == id) is { } action ? ActionName(action) : id));
        Publish();
    }

    public void SetLibrary(InteractionPanelLibraryEvent data)
    {
        _custom = data.Actions;
        _definitions = _standard.Concat(_custom.Select(a => a.Definition())).ToArray();
        _available.RemoveWhere(id => id.StartsWith("custom:", StringComparison.Ordinal));
        _available.UnionWith(data.Available);
        _libraryLoaded = true;
        _waiting = false;
        _libraryRevision++;
        _librarySaved = data.Message == "dl-interaction-panel-library-saved";
        _libraryStatus = data.Message.Length == 0 ? "" : Loc.GetString(data.Message);
        Publish();
    }

    private void Publish(bool arousalOnly = false, bool gearOnly = false)
    {
        var state = new TguiData().String("name", _data.Name).Bool("self", _data.Self)
            .Number("arousalCurrent", _arousalCurrent).Number("arousalMaximum", _arousalMaximum)
            .Bool("arousalOnly", arousalOnly)
            .Bool("gearOnly", gearOnly)
            .String("heldName", _gear?.HeldName ?? "").String("heldSlot", _gear?.HeldSlot ?? "")
            .Array("gear", (_gear?.Entries ?? []).Select(g => new TguiData().String("slot", g.Slot).String("slotName", Loc.GetString("dl-lustwish-slot-" + g.Slot)).String("name", g.Name)
                .Bool("locked", g.Locked).Bool("active", g.Active).Bool("electronic", g.Electronic).Bool("shock", g.Shock).Bool("hypnosis", g.Hypnosis)
                .Number("intensity", g.Intensity).Bool("colorable", g.Colorable).Bool("leashed", g.Leashed)))
            .Array("gearColors", LustWishEquipment.Colors.Select(c => new TguiData().String("id", c).String("name", c)))
            .Array("bodyFeatures", _bodyFeatures.Where(f => f.Enabled && f.Part != "womb").Select(f =>
                new TguiData().String("part", f.Part).String("name", BodyCustomizationCatalog.Names[f.Part]).Number("visibility", f.Visibility)))
            .Bool("humanoid", _data.Humanoid).Bool("debug", _data.DebugPreferences)
            .Number("interval", Interval).String("status", _status).Object("labels", _labels)
            .Bool("libraryLoaded", _libraryLoaded).Bool("waiting", _waiting).String("libraryStatus", _libraryStatus)
            .Number("libraryRevision", _libraryRevision).Bool("librarySaved", _librarySaved)
            .Object("targetDoll", new TguiData().String("base", _dollTexture).Object("parts", _dollParts))
            .Array("regions", InteractionPanelCustomRules.Bodies.Select(id => new TguiData().String("id", id)
                .String("name", Loc.GetString("dl-interaction-panel-body-" + id))))
            .Array("categories", Enum.GetValues<InteractionPanelCategory>().Select(category => new TguiData()
                .Number("id", (int) category).String("name", Loc.GetString("dl-interaction-panel-category-" + category.ToString().ToLowerInvariant()))))
            .Array("sounds", _sounds)
            .Array("actions", _definitions.Where(a => a.Supports(_data.Self, _data.Humanoid) && a.SupportsSexTraits(_data.UserSexTraits, _data.TargetSexTraits))
                .Select(a => new TguiData().String("id", a.Id).String("name", ActionName(a)).String("body", a.Body)
                    .Number("category", (int) a.Category).Bool("available", _available.Contains(a.Id))
                    .Bool("favorite", _favorites.Contains(a.Id)).Bool("repeating", _repeating.Contains(a.Id))))
            .Array("custom", _custom.Select(a => new TguiData().String("id", a.Id).String("title", a.Title)
                .String("template", a.Template).String("body", a.Body).Number("category", (int) a.Category)
                .Number("flags", (int) a.Flags).String("color", a.ChatColor).String("sound", a.Sound)));
        _panel.SetState("InteractionPanel", state.ToString(), Loc.GetString("dl-interaction-panel-open"));
    }

    private void OnAction(string action, string payload)
    {
        if (action == "close") { Close(); return; }
        if (action == "focus") { _panel.Web.FocusTextInput(); return; }
        if (action == "stop") { RepeatRequested?.Invoke("", false, Interval); return; }
        if (!TguiActionData.TryParse(payload, out var parsed) || parsed == null) return;
        var id = parsed.String("id") ?? "";
        if (action == "gear")
        {
            GearRequested?.Invoke(parsed.String("operation") ?? "", parsed.String("slot") ?? "", parsed.String("value") ?? "");
            return;
        }
        if (action == "body-visibility" && parsed.TryInt("value", out var visibility) && visibility is >= 0 and <= 2
            && _bodyFeatures.Any(f => f.Enabled && f.Part == id && f.Part != "womb"))
        {
            BodyVisibilityRequested?.Invoke(id, visibility);
            return;
        }
        if (action == "interval" && parsed.TryFloat("value", out var interval))
        {
            Interval = InteractionPanelActions.ClampInterval(interval);
            PreferencesChanged?.Invoke();
            foreach (var repeating in _repeating.ToArray()) RepeatRequested?.Invoke(repeating, true, Interval);
            Publish();
        }
        else if (action == "favorite" && _definitions.Any(a => a.Id == id))
        {
            if (!_favorites.Add(id)) _favorites.Remove(id);
            PreferencesChanged?.Invoke();
            Publish();
        }
        else if (action == "run" && _available.Contains(id)) ActionRequested?.Invoke(id);
        else if (action == "repeat" && _available.Contains(id)) RepeatRequested?.Invoke(id, !_repeating.Contains(id), Interval);
        else if (action == "delete" && _libraryLoaded && !_waiting && _custom.Any(a => a.Id == id))
        {
            _waiting = true;
            _libraryStatus = Loc.GetString("dl-interaction-panel-library-busy");
            Publish();
            DeleteRequested?.Invoke(id);
        }
        else if (action == "save" && _libraryLoaded && !_waiting && parsed.TryInt("category", out var category) && parsed.TryInt("flags", out var flags))
        {
            if (category is < 0 or > 255 || flags is < 0 or > 255 || id.Length > 0 && !_custom.Any(a => a.Id == id)) return;
            var custom = new InteractionPanelCustomAction
            {
                Id = id, Title = (parsed.String("title") ?? "").Trim(), Template = (parsed.String("template") ?? "").Trim(),
                Category = (InteractionPanelCategory) category, Body = parsed.String("body") ?? "all",
                Flags = (InteractionPanelActionFlags) flags, ChatColor = (parsed.String("color") ?? "").Trim(),
                Sound = string.IsNullOrEmpty(parsed.String("sound")) ? null : parsed.String("sound"),
            };
            var error = InteractionPanelCustomRules.ValidationError(custom);
            if (error != null || custom.Sound != null && !_soundIds.Contains(custom.Sound))
            {
                _libraryStatus = Loc.GetString(error ?? "dl-interaction-panel-library-invalid");
                Publish();
                return;
            }
            _waiting = true;
            _libraryStatus = Loc.GetString("dl-interaction-panel-library-busy");
            Publish();
            SaveRequested?.Invoke(custom);
        }
    }

    protected override void Draw(DrawingHandleScreen handle) => CharacterWindowFrame.Draw(handle, PixelSize, UIScale, Resizable);

    private void UpdateDollTexture()
    {
        var path = Theme.Path.ToString();
        if (path.StartsWith("/Textures/", StringComparison.Ordinal)) path = path[10..];
        _dollTexture = _images.Frame(new SpriteSpecifier.Texture(new ResPath(path + "/target_doll.png")));
        if (_dollTexture.Length == 0)
            _dollTexture = _images.Frame(new SpriteSpecifier.Texture(new ResPath("Interface/Default/target_doll.png")));
    }

    protected override void OnThemeUpdated()
    {
        base.OnThemeUpdated();
        if (_dollParts == null) return;
        UpdateDollTexture();
        Publish();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (Parent == null) return;
        if (UpdateArousal()) Publish(arousalOnly: true);
        MinSize = Vector2.Min(new Vector2(360, 420), Parent.Size);
        if (Size.X > Parent.Size.X || Size.Y > Parent.Size.Y) SetSize = Vector2.Min(Size, Parent.Size);
        LayoutContainer.SetPosition(this, Vector2.Clamp(Position, Vector2.Zero, Vector2.Max(Vector2.Zero, Parent.Size - Size)));
    }

    protected override DragMode GetDragModeFor(Vector2 pointer)
    {
        if (!Resizable) return DragMode.None;
        var mode = DragMode.None;
        if (pointer.X < 8) mode |= DragMode.Left;
        else if (pointer.X > Size.X - 8) mode |= DragMode.Right;
        if (pointer.Y < 8) mode |= DragMode.Top;
        else if (pointer.Y > Size.Y - 8) mode |= DragMode.Bottom;
        return mode;
    }

    private static readonly string[] LabelKeys =
    [
        "open", "arousal", "self-mode", "debug", "search", "clear", "favorites", "favorite-toggle", "empty", "stop", "repeat", "speed",
        "action-hint", "unavailable", "tab-actions", "tab-custom", "editor-help", "editor-title", "editor-template", "editor-example",
        "editor-preference", "editor-region", "editor-self", "editor-other", "editor-humanoid", "editor-animal", "editor-color",
        "editor-sound", "editor-new", "editor-save", "editor-delete", "editor-you", "editor-target", "sound-none", "edit", "back",
        "library-busy", "my-list-empty", "library-full", "validation-title-required", "validation-title-long", "validation-template-start",
        "validation-template-long", "validation-tokens", "validation-hidden", "validation-targets", "validation-actors", "validation-color",
        "compact-target", "compact-expand", "compact-collapse", "compact-available", "compact-found", "compact-close", "compact-preview",
        "tab-body", "body-own", "body-empty", "body-hidden", "body-under", "body-over",
        "tab-gear", "gear-help", "gear-empty", "gear-held", "gear-attach", "gear-remove", "gear-escape", "gear-release-all",
        "gear-lock", "gear-unlock", "gear-pair", "gear-toggle", "gear-mode", "gear-pulse", "gear-leash", "gear-unleash", "gear-color",
        "compact-delete-confirm", "compact-cancel", "compact-locked", "compact-all", "compact-help", "compact-seconds", "sounds-all", "compact-available-only", "compact-body-hint", "body-all",
    ];
}

internal sealed class InteractionPanelHost : Container
{
    private readonly TguiPanel _panel;
    private readonly InteractionPanelSpriteView _preview;

    public InteractionPanelHost(TguiPanel panel, InteractionPanelSpriteView preview)
    {
        _panel = panel;
        _preview = preview;
        HorizontalExpand = VerticalExpand = true;
        AddChild(panel);
        AddChild(preview);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _panel.Measure(availableSize);
        _preview.Measure(new Vector2(64, 64));
        return Vector2.Zero;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        _panel.Arrange(UIBox2.FromDimensions(Vector2.Zero, finalSize));
        _preview.Arrange(UIBox2.FromDimensions(new Vector2(12, 46), new Vector2(64, 64)));
        return finalSize;
    }
}
