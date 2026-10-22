using Content.Client._DeepLagoon.Search; // DeepLagoon: fuzzy search
using System.Linq;
using Content.Shared._DeepLagoon.DiscordLink;
using Robust.Shared.Timing;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Client.Inventory;
using Content.Shared.Inventory;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Robust.Shared.Input;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI.Loadouts;

/// <summary>The Einstein category browser, backed by the target's saved role loadouts.</summary>
public sealed partial class PersonalLoadoutEditor : BoxContainer
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IResourceCache _resources = default!;
    private readonly Label _points = new();
    private readonly ProgressBar _bar = new() { MaxHeight = 8, Margin = new Thickness(0, 5) };
    private readonly Button _showUnavailable = new() { ToggleMode = true, Text = Loc.GetString("dl-loadout-show-unavailable") };
    private readonly Button _removeUnavailable = new() { Text = Loc.GetString("dl-loadout-remove-unavailable") };
    private readonly LineEdit _search = new() { HorizontalExpand = true, PlaceHolder = Loc.GetString("dl-loadout-search") };
    private readonly BoxContainer _body = new() { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, VerticalExpand = true };
    private readonly Dictionary<string, string> _selectedCategories = new();
    private HumanoidCharacterProfile? _profile;
    private ICommonSession? _session;
    private string _job = string.Empty;
    private bool _refreshing;
    private SlotDefinition? _slotFilter;
    private string? _pendingSlotCategory;
    private string? _customizeId;
    private readonly Label _slotTitle = new();
    private readonly Button _allSlots = new() { Text = Loc.GetString("dl-loadout-all-items") };
    private readonly Dictionary<string, SlotButton> _slotButtons = new();
    public event Action? SlotOpened;
    public event Action<RoleLoadout>? SelectionChanged;
    public event Action<HumanoidCharacterProfile>? ProfileChanged;

    public PersonalLoadoutEditor()
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;
        InitializeJobBrowser();
        AddChild(_points);
        AddChild(_bar);
        var buttons = new BoxContainer { HorizontalExpand = true };
        buttons.AddChild(_showUnavailable);
        buttons.AddChild(_removeUnavailable);
        AddChild(buttons);
        var slotFilter = new BoxContainer { HorizontalExpand = true };
        slotFilter.AddChild(_slotTitle);
        slotFilter.AddChild(new Control { HorizontalExpand = true });
        slotFilter.AddChild(_allSlots);
        AddChild(slotFilter);
        _allSlots.OnPressed += _ => { _slotFilter = null; UpdateSlotHighlights(); Rebuild(); };
        AddChild(_search);
        AddChild(_body);
        _showUnavailable.OnToggled += _ => Rebuild();
        _search.OnTextChanged += _ => Rebuild();
        _removeUnavailable.OnPressed += _ => RemoveUnavailable();
    }

    private bool _hadDiscordRewards;
    private int _lastBoostyTier;
    private readonly int[] _donorColors = new int[4];

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        var supporters = _entities.System<SharedDiscordBoostSystem>();
        var discordRewards = supporters.HasDiscordRewardAccess(_session);
        var boostyTier = supporters.GetSupporterTier(_session);
        if (discordRewards == _hadDiscordRewards && boostyTier == _lastBoostyTier && _donorColors.SequenceEqual(supporters.RoleColors))
            return;
        _hadDiscordRewards = discordRewards;
        _lastBoostyTier = boostyTier;
        Array.Copy(supporters.RoleColors, _donorColors, 4);
        Rebuild();
    }

    public void Refresh(HumanoidCharacterProfile? profile, string job, ICommonSession? session)
    {
        _profile = profile;
        _job = job;
        _session = session;
        Rebuild();
    }

    private RoleLoadout GetRole()
    {
        var role = _profile!.Loadouts.TryGetValue(PersonalLoadoutSystem.Role, out var stored) ? stored.Clone() : new RoleLoadout(PersonalLoadoutSystem.Role);
        role.EnsureValid(_profile, _session, IoCManager.Instance!);
        return role;
    }

    private void Remember(Control control)
    {
        if (control is NeoTabContainer tabs)
        {
            if (tabs.CurrentControl?.Name is { } selected)
                _selectedCategories[tabs.Name ?? "Root"] = selected;
            foreach (var child in tabs.Contents)
                Remember(child);
        }
        else
        {
            foreach (var child in control.Children)
                Remember(child);
        }
    }

    private void Rebuild()
    {
        if (TguiMode) return;
        if (_refreshing)
            return;
        _refreshing = true;
        Remember(_body);
        if (_pendingSlotCategory != null)
        {
            _selectedCategories["Root"] = _pendingSlotCategory;
            _pendingSlotCategory = null;
        }
        _body.DisposeAllChildren();
        if (_profile == null || !_prototypes.HasIndex<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role))
        {
            _refreshing = false;
            return;
        }
        _slotTitle.Text = _slotFilter == null ? Loc.GetString("dl-loadout-all-items") : Loc.GetString("dl-loadout-slot-items", ("slot", SlotLabel(_slotFilter)));
        _allSlots.Visible = _slotFilter != null;
        PrepareJobCatalog();
        var system = _entities.System<PersonalLoadoutSystem>();
        var role = GetRole();
        var selected = role.SelectedLoadouts.Values.SelectMany(x => x).ToDictionary(x => x.Prototype.Id);
        var spent = selected.Values.Sum(x => _prototypes.Index(x.Prototype).PersonalCost);
        _points.Text = Loc.GetString("dl-loadout-points", ("points", Math.Max(0, system.GetPoints(_session) - spent)), ("max", system.GetPoints(_session)));
        _bar.MaxValue = Math.Max(1, system.GetPoints(_session));
        _bar.Value = Math.Max(0, system.GetPoints(_session) - spent);
        var all = _prototypes.EnumeratePrototypes<LoadoutPrototype>().Where(x => x.PersonalItems.Count > 0).ToList();
        var filtered = all.Where(x => _slotFilter == null || FitsSlot(x, _slotFilter)).Where(x => _showUnavailable.Pressed || DonorCategoryOf(x) != DonorCategory.None || selected.ContainsKey(x.ID) || system.CanUse(x, _profile, _job, _session, out _))
            .ToList();
        filtered = FuzzySearch.Filter(filtered, _search.Text, x => _prototypes.Index(x.PersonalItems[0]).Name, x => x.ID); // DeepLagoon: fuzzy search
        if (!string.IsNullOrWhiteSpace(_search.Text))
        {
            _body.AddChild(MakeList(filtered, role, selected, spent));
        }
        else
        {
            var categories = _prototypes.EnumeratePrototypes<PersonalLoadoutCategoryPrototype>().ToDictionary(x => x.ID);
            var roots = categories.Values.Where(x => x.Root).OrderBy(x => x.ID == "Uncategorized" ? "" : x.ID).ToList();
            _body.AddChild(MakeTabs("Root", roots, categories, filtered, role, selected, spent, new HashSet<string>()));
        }
        _removeUnavailable.Disabled = !all.Any(x => selected.ContainsKey(x.ID) && !system.CanUse(x, _profile, _job, _session, out _));
        _refreshing = false;
    }

    private Control MakeTabs(string name, List<PersonalLoadoutCategoryPrototype> categories, Dictionary<string, PersonalLoadoutCategoryPrototype> index, List<LoadoutPrototype> loadouts, RoleLoadout role, Dictionary<string, Loadout> selected, int spent, HashSet<string> seen)
    {
        var tabs = new NeoTabContainer { Name = name, VerticalExpand = true, HorizontalExpand = true };
        foreach (var category in categories)
        {
            if (seen.Contains(category.ID))
                continue;
            var descendants = Descendants(category, index, new HashSet<string>());
            if (!loadouts.Any(x => descendants.Contains(x.PersonalCategory)) && !FilteredJobEntries().Any(x => descendants.Contains(x.Category)) && !(_slotFilter != null && _selectedCategories.GetValueOrDefault("Root") == category.ID))
                continue;
            var content = new BoxContainer { Name = category.ID, Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, VerticalExpand = true };
            var children = category.SubCategories.Where(x => index.ContainsKey(x.Id)).Select(x => index[x.Id]).ToList();
            if (children.Count == 0)
                content.AddChild(MakeList(loadouts.Where(x => x.PersonalCategory == category.ID).ToList(), role, selected, spent, category.ID));
            else
            {
                var nested = new HashSet<string>(seen) { category.ID };
                content.AddChild(MakeTabs(category.ID, children, index, loadouts, role, selected, spent, nested));
            }
            var label = "loadout-category-" + category.ID;
            tabs.AddTab(content, Loc.TryGetString(label, out var localized) ? localized : category.ID);
        }
        if (_selectedCategories.TryGetValue(name, out var previous))
        {
            var content = tabs.Contents.FirstOrDefault(x => x.Name == previous);
            if (content != null)
                tabs.SelectTab(content);
        }
        return tabs;
    }

    private static HashSet<string> Descendants(PersonalLoadoutCategoryPrototype category, Dictionary<string, PersonalLoadoutCategoryPrototype> index, HashSet<string> seen)
    {
        if (!seen.Add(category.ID))
            return seen;
        foreach (var id in category.SubCategories)
            if (index.TryGetValue(id.Id, out var child))
                Descendants(child, index, seen);
        return seen;
    }

    private Control MakeList(List<LoadoutPrototype> loadouts, RoleLoadout role, Dictionary<string, Loadout> selected, int spent, string? category = null)
    {
        var container = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, VerticalExpand = true };
        var scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        var grid = new TileGrid { HorizontalExpand = true, HSeparationOverride = 6, VSeparationOverride = 6 };
        scroll.AddChild(grid);
        container.AddChild(scroll);
        var system = _entities.System<PersonalLoadoutSystem>();
        var sprites = _entities.System<SpriteSystem>();
        AddJobCards(grid, category);
        foreach (var prototype in loadouts.OrderBy(x => _prototypes.Index(x.PersonalItems[0]).Name))
        {
            var usable = system.CanUse(prototype, _profile!, _job, _session, out var reason);
            var pressed = selected.TryGetValue(prototype.ID, out var selection);
            var savedColor = selection?.Customization?.Color is { } savedHex && Color.TryFromHex(savedHex, out var parsedColor) ? parsedColor : Color.White;
            var title = _prototypes.Index(prototype.PersonalItems[0]).Name;
            var donor = DonorCategoryOf(prototype);
            var donorLabel = donor == DonorCategory.Boosty
                ? Loc.GetString("dl-loadout-donor-boosty", ("tier", prototype.PersonalDonorTier))
                : donor == DonorCategory.DiscordBoost ? Loc.GetString("dl-loadout-donor-discord") : string.Empty;
            var tooltip = string.Join("\n", new[] { title, donorLabel, reason }.Where(x => !string.IsNullOrEmpty(x)));
            var card = new BoxContainer { Orientation = LayoutOrientation.Vertical, SetWidth = 156, ToolTip = tooltip };
            var button = new Button { ToggleMode = true, Pressed = pressed, HorizontalExpand = true, MinHeight = 124, Disabled = !pressed && (!usable || spent + prototype.PersonalCost > system.GetPoints(_session)), ToolTip = tooltip };
            if (donor != DonorCategory.None)
            {
                var accent = _entities.System<SharedDiscordBoostSystem>().GetDonorColor(donor, prototype.PersonalDonorTier);
                button.StyleBoxOverride = new StyleBoxFlat
                {
                    BackgroundColor = Color.InterpolateBetween(new Color(0.08f, 0.09f, 0.12f), accent, usable ? 0.25f : 0.12f),
                    BorderColor = pressed && usable ? Color.White : accent,
                    BorderThickness = new Thickness(pressed && usable ? 2 : 1),
                };
            }
            var content = new BoxContainer { Orientation = LayoutOrientation.Vertical, Margin = new Thickness(4), MouseFilter = MouseFilterMode.Ignore };
            var icon = new TextureRect { Texture = sprites.GetPrototypeIcon(prototype.PersonalItems[0]).Default, SetSize = new Vector2(48, 48), HorizontalAlignment = HAlignment.Center, Stretch = TextureRect.StretchMode.KeepAspectCentered, Modulate = usable ? savedColor : savedColor.WithAlpha(0.4f), MouseFilter = MouseFilterMode.Ignore };
            var iconHolder = new Control { SetSize = new Vector2(56, 48), HorizontalAlignment = HAlignment.Center, MouseFilter = MouseFilterMode.Ignore };
            iconHolder.AddChild(icon);
            if (!usable)
                iconHolder.AddChild(new TextureRect
                {
                    Texture = _resources.GetResource<TextureResource>("/Textures/Interface/Nano/lock.svg.192dpi.png").Texture,
                    SetSize = new Vector2(22, 22), HorizontalAlignment = HAlignment.Right, VerticalAlignment = VAlignment.Bottom,
                    Stretch = TextureRect.StretchMode.KeepAspectCentered, MouseFilter = MouseFilterMode.Ignore,
                });
            content.AddChild(iconHolder);
            if (donor != DonorCategory.None)
                content.AddChild(new Label { Text = donorLabel, MouseFilter = MouseFilterMode.Ignore });
            content.AddChild(new RichTextLabel { Text = title, HorizontalExpand = true, MinHeight = 36, MaxWidth = 140, MouseFilter = MouseFilterMode.Ignore });
            var footer = new BoxContainer { HorizontalExpand = true, MouseFilter = MouseFilterMode.Ignore };
            footer.AddChild(new Label { Text = Loc.GetString("dl-loadout-item-cost", ("cost", prototype.PersonalCost)), HorizontalExpand = true, MouseFilter = MouseFilterMode.Ignore });
            content.AddChild(footer);
            button.AddChild(content);
            button.OnPressed += _ => Select(prototype, button.Pressed);
            card.AddChild(button);
            if (pressed && usable && selection != null)
            {
                var customize = new Button { Text = "\u2699", ToolTip = Loc.GetString("dl-loadout-customize"), ToggleMode = true, Pressed = _customizeId == prototype.ID, SetWidth = 28 };
                customize.OnToggled += args => { _customizeId = args.Pressed ? prototype.ID : null; Rebuild(); };
                footer.AddChild(customize);
            }
            grid.AddChild(card);
        }
        if (grid.ChildCount == 0)
            grid.AddChild(new Label { Text = Loc.GetString("dl-loadout-no-items") });
        if (_customizeId != null && selected.TryGetValue(_customizeId, out var focused) && loadouts.FirstOrDefault(x => x.ID == _customizeId) is { } focusedPrototype && system.CanUse(focusedPrototype, _profile!, _job, _session, out _))
            container.AddChild(MakeCustomization(focusedPrototype, focused));
        return container;
    }

    private static DonorCategory DonorCategoryOf(LoadoutPrototype prototype)
        => prototype.PersonalDonor != DonorCategory.None ? prototype.PersonalDonor
            : prototype.PersonalRequirements.Any(r => r.Kind == "DiscordBoostRequirement") ? DonorCategory.DiscordBoost : DonorCategory.None;

    private Control MakeCustomization(LoadoutPrototype prototype, Loadout selection)
    {
        var system = _entities.System<PersonalLoadoutSystem>();
        var savedColor = selection.Customization?.Color is { } savedHex && Color.TryFromHex(savedHex, out var parsed) ? parsed : Color.White;
        var panel = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, Margin = new Thickness(4) };
        panel.AddChild(new RichTextLabel { Text = _prototypes.Index(prototype.PersonalItems[0]).Name, HorizontalExpand = true });
        var icon = new TextureRect { Texture = _entities.System<SpriteSystem>().GetPrototypeIcon(prototype.PersonalItems[0]).Default, SetSize = new Vector2(48, 48), Modulate = savedColor, Stretch = TextureRect.StretchMode.KeepAspectCentered };
        panel.AddChild(icon);
        var scroll = new ScrollContainer { HorizontalExpand = true, HScrollEnabled = false, MaxHeight = 240 };
        var fields = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true };
        scroll.AddChild(fields);
        panel.AddChild(scroll);
        var name = new LineEdit { Text = selection.Customization?.Name ?? "", PlaceHolder = Loc.GetString("dl-loadout-name"), HorizontalExpand = true, Visible = prototype.PersonalCustomName };
        var description = new LineEdit { Text = selection.Customization?.Description ?? "", PlaceHolder = Loc.GetString("dl-loadout-description"), HorizontalExpand = true, Visible = prototype.PersonalCustomDescription };
        var paint = new Button
        {
            Text = Loc.GetString("dl-loadout-paint"),
            ToolTip = Loc.GetString("dl-loadout-paint-tooltip"),
            ToggleMode = true,
            Pressed = selection.Customization?.Color != null,
            Visible = prototype.PersonalCustomColor,
            HorizontalExpand = true,
        };
        var color = new ColorSelectorSliders
        {
            Color = savedColor,
            HorizontalExpand = true,
            Visible = prototype.PersonalCustomColor && paint.Pressed,
        };
        paint.OnToggled += args =>
        {
            color.Visible = args.Pressed;
            icon.Modulate = args.Pressed ? color.Color : Color.White;
        };
        color.OnColorChanged += value => icon.Modulate = paint.Pressed ? value : Color.White;
        var heirloom = new CheckBox { Text = Loc.GetString("dl-loadout-heirloom"), Pressed = selection.Customization?.Heirloom ?? false, Visible = prototype.PersonalHeirloom };
        fields.AddChild(name); fields.AddChild(description); fields.AddChild(paint); fields.AddChild(color); fields.AddChild(heirloom);
        var save = new Button { Text = Loc.GetString("dl-loadout-customize-save") };
        fields.AddChild(save);
        save.OnPressed += _ =>
        {
            var updated = GetRole();
            foreach (var group in updated.SelectedLoadouts.Values)
                for (var i = 0; i < group.Count; i++)
                    if (group[i].Prototype.Id == prototype.ID)
                        group[i] = new Loadout { Prototype = prototype.ID, Customization = system.Sanitize(prototype, new PersonalLoadoutCustomization { Name = name.Text, Description = description.Text, Color = paint.Pressed ? color.Color.ToHex() : null, Heirloom = heirloom.Pressed }) };
            SelectionChanged?.Invoke(updated);
        };
        return panel;
    }

    private sealed class TileGrid : GridContainer
    {
        private float _gridWidth;

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            var width = float.IsFinite(availableSize.X) ? Math.Max(156, availableSize.X - 12) : 640;
            if (Math.Abs(width - _gridWidth) > 0.1f)
            {
                _gridWidth = width;
                MaxGridWidth = width;
            }
            return base.MeasureOverride(availableSize);
        }
    }

    public void UpdatePreviewSlots(EntityUid preview, BoxContainer left, BoxContainer right)
    {
        left.DisposeAllChildren();
        right.DisposeAllChildren();
        _slotButtons.Clear();
        var inventory = _entities.System<InventorySystem>();
        if (!inventory.TryGetSlots(preview, out var slots))
            return;
        var visibleSlots = FilterPreviewSlots(slots)
            .OrderBy(s => s.StrippingWindowPos.Y).ThenBy(s => s.StrippingWindowPos.X).ToList();
        for (var i = 0; i < visibleSlots.Count; i++)
        {
            var slot = visibleSlots[i];
            var button = new SlotButton(new ClientInventorySystem.SlotData(slot)) { ToolTip = SlotLabel(slot), SetSize = new Vector2(64, 64) };
            if (inventory.TryGetSlotEntity(preview, slot.Name, out var item))
                button.SetEntity(item);
            button.Pressed += (args, _) =>
            {
                if (args.Function != EngineKeyFunctions.UIClick)
                    return;
                _slotFilter = slot;
                _pendingSlotCategory = slot.Name.ToLowerInvariant() switch
                {
                    "jumpsuit" => "Uniform",
                    "outerclothing" => "Outer",
                    "shoes" => "Shoes",
                    "gloves" => "Hands",
                    "head" or "ears" or "helmetcover" or "helmetattachment" => "Head",
                    "eyes" => "Eyes",
                    "mask" or "balaclava" => "Mask",
                    "neck" or "armbandright" or "armbandleft" => "Neck",
                    "back" => "Backpacks",
                    "belt" => "Belt",
                    _ => "Items",
                };
                _search.Text = string.Empty;
                UpdateSlotHighlights();
                Rebuild();
                SlotOpened?.Invoke();
                args.Handle();
            };
            (i % 2 == 0 ? left : right).AddChild(button);
            _slotButtons[slot.Name] = button;
        }
        if (_slotFilter != null)
            _slotFilter = visibleSlots.FirstOrDefault(s => s.Name == _slotFilter.Name);
        UpdateSlotHighlights();
    }

    private void UpdateSlotHighlights()
    {
        foreach (var (name, button) in _slotButtons)
            button.Highlight = _slotFilter?.Name == name;
    }

    private bool FitsSlot(LoadoutPrototype loadout, SlotDefinition slot)
    {
        return loadout.PersonalItems.Any(id => _prototypes.Index(id).TryGetComponent<ClothingComponent>(out var clothing) && (clothing.Slots & slot.SlotFlags) != 0);
    }

    private IEnumerable<SlotDefinition> FilterPreviewSlots(IEnumerable<SlotDefinition> slots)
    {
        if (_profile == null)
            return Enumerable.Empty<SlotDefinition>();

        var personalItems = _prototypes.EnumeratePrototypes<LoadoutPrototype>()
            .Where(item => item.PersonalItems.Count > 0).ToArray();
        var jobItems = Array.Empty<LoadoutPrototype>();
        if (_prototypes.TryIndex<RoleLoadoutPrototype>(LoadoutSystem.GetJobPrototype(_job), out var role))
        {
            var loadout = _profile.GetLoadoutOrDefault(role.ID, _session, _profile.Species, _entities, _prototypes);
            jobItems = role.Groups.Select(id => _prototypes.Index(id)).Where(group => !group.Hidden)
                .SelectMany(group => group.Loadouts.Concat(group.Subgroups.SelectMany(id => _prototypes.Index(id).Loadouts)))
                .Distinct().Where(id => !loadout.IsHidden(_profile, _session, id, IoCManager.Instance!))
                .Select(id => _prototypes.Index(id)).ToArray();
        }

        // Keep empty slots usable when equipment offers a replacement for them.
        return slots.Where(slot => (slot.SlotFlags & (SlotFlags.PREVENTEQUIP | SlotFlags.POCKET | SlotFlags.SUITSTORAGE)) == 0
            && (personalItems.Any(item => FitsSlot(item, slot)) || jobItems.Any(item => FitsJobSlot(item, slot))));
    }

    private static string SlotLabel(SlotDefinition slot)
    {
        var key = "dl-loadout-slot-" + slot.Name.ToLowerInvariant();
        return Loc.TryGetString(key, out var label) ? label : slot.DisplayName;
    }

    private void Select(LoadoutPrototype prototype, bool pressed)
    {
        var role = GetRole();
        var group = _prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups.FirstOrDefault(g => _prototypes.Index(g).Loadouts.Any(l => l.Id == prototype.ID));
        if (pressed)
            role.AddLoadout(group, prototype.ID, _prototypes);
        else
            role.RemoveLoadout(group, prototype.ID, _prototypes);
        var profile = _profile!.WithLoadout(role);
        if (pressed && _jobRole != null)
            profile = profile.WithLoadout(_entities.System<PersonalLoadoutSystem>().RemoveJobConflicts(profile, _jobRole, prototype));
        if (pressed)
            profile = _entities.System<PersonalLoadoutSystem>().RestoreEquipmentSlots(profile, _job, prototype);
        ProfileChanged?.Invoke(profile);
    }

    private bool CanAffordPersonal(LoadoutPrototype prototype, RoleLoadout role)
    {
        var candidate = role.Clone();
        var groups = _prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups
            .Where(g => _prototypes.Index(g).Loadouts.Any(item => item.Id == prototype.ID)).Take(1).ToArray();
        if (groups.Length == 0) return false;
        candidate.AddLoadout(groups[0], prototype.ID, _prototypes);
        return candidate.SelectedLoadouts.Values.SelectMany(items => items)
            .Sum(item => _prototypes.Index(item.Prototype).PersonalCost) <= _entities.System<PersonalLoadoutSystem>().GetPoints(_session);
    }

    private void RemoveUnavailable()
    {
        if (_profile == null)
            return;
        var role = GetRole();
        var system = _entities.System<PersonalLoadoutSystem>();
        foreach (var group in role.SelectedLoadouts.Values)
            group.RemoveAll(x => !_prototypes.TryIndex(x.Prototype, out var prototype) || !system.CanUse(prototype, _profile, _job, _session, out _));
        SelectionChanged?.Invoke(role);
    }
}
