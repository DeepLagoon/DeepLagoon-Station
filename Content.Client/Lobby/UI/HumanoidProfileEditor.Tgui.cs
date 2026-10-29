using System.IO;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Systems.Guidebook;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared._Mono.Company;
using Content.Shared.Guidebook;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Utility;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.Localization;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    public Func<TguiData>? SetupState;
    public Func<string, TguiActionData, bool>? SetupAction;
    public void RefreshTguiSetup() => PublishTguiProfile();
    private TguiPanel? _profileWeb;
    private TguiEditorHost? _tguiHost;
    private HumanoidCharacterProfile? _publishedTguiProfile;
    private bool _publishedTguiDirty;
    private bool _publishedTguiClothes;
    private bool _publishedTattoosBanned;
    private CharacterEditorWindow? EditorWindow
    {
        get
        {
            for (Control? parent = Parent; parent != null; parent = parent.Parent)
                if (parent is CharacterEditorWindow window) return window;
            return null;
        }
    }
    private int _publishedSupporterBonus = -1;
    private float _tguiRefresh;
    private bool _tguiPublishPending;
    private static TguiSpriteImages _tguiImages => TguiSpriteImages.Shared;
    private readonly Dictionary<int, (string Id, string Image, string Status)> _headshotPreviews = new();
    private readonly Dictionary<int, Guid> _headshotReads = new();
    private string _tguiMode = "appearance";
    private readonly Content.Client._DeepLagoon.Tattoos.TattooEditorData _tattooEditor = new();
    private string _tattooStatus = "";
    private bool _tattooFileBusy;

    private async void HandleTattooBake(TguiActionData args)
    {
        if (Profile == null || args.String("recipe") is not { } recipe
            || !Content.Client._DeepLagoon.Tattoos.TattooEditorData.TryApply("tattoo-transform", args, Profile.Appearance.Tattoos, out var next, out _tattooStatus)
            || next.FirstOrDefault(t => t.Id == args.String("id")) is not { } basis) return;
        var slot = CharacterSlot; var original = Profile;
        _tattooFileBusy = true; _tattooStatus = "Сервер подтверждает рисунок…"; PublishTguiProfile();
        try
        {
            var result = await IoCManager.Resolve<IEntityManager>().System<Content.Client._DeepLagoon.Tattoos.TattooArtworkSystem>().Authorize(basis, recipe);
            if (Disposed || (_profileWeb == null && _nativeTattoos == null) || Profile != original || CharacterSlot != slot
                || _requirements.TattoosBanned || !_cfgManager.GetCVar(Content.Shared._DeepLagoon.Tattoos.TattooCVars.Enabled)) return;
            var appearance = Profile.Appearance.Clone(); next[next.FindIndex(t => t.Id == basis.Id)] = result;
            _appearanceRedo = null;
            appearance.Tattoos = next; Profile = Profile.WithCharacterAppearance(appearance);
            _tattooStatus = ""; ReloadProfilePreview(); SetDirty();
        }
        catch (Exception e) { _tattooStatus = e.Message; }
        finally { _tattooFileBusy = false; if (_profileWeb != null && CharacterSlot == slot) PublishTguiProfile(); }
    }

    private async void HandleTattooFileAction(string action, Content.Shared._DeepLagoon.Tattoos.Tattoo template)
    {
        var slot = CharacterSlot; _tattooFileBusy = true; _tattooStatus = "Выбор PNG…"; PublishTguiProfile();
        try
        {
            if (action == "tattoo-export")
                _tattooStatus = await Content.Client._DeepLagoon.Tattoos.TattooFileDialogs.Export(template);
            else
            {
                var result = await Content.Client._DeepLagoon.Tattoos.TattooFileDialogs.Import(template);
                if (Disposed || (_profileWeb == null && _nativeTattoos == null) || Profile == null || CharacterSlot != slot) return;
                _tattooStatus = result.Status;
                if (result.Tattoo == null) return;
                if (_requirements.TattoosBanned || !_cfgManager.GetCVar(Content.Shared._DeepLagoon.Tattoos.TattooCVars.Enabled)
                    || !Profile.Appearance.Tattoos.Any(t => t.ContentEquals(template)))
                { _tattooStatus = "Настройки или рисунок изменились. Повторите загрузку PNG."; return; }
                var appearance = Profile.Appearance.Clone();
                appearance.Tattoos[appearance.Tattoos.FindIndex(t => t.Id == template.Id)] = result.Tattoo;
                _appearanceRedo = null;
                Profile = Profile.WithCharacterAppearance(appearance); ReloadProfilePreview(); SetDirty();
            }
        }
        finally { _tattooFileBusy = false; if (_profileWeb != null && CharacterSlot == slot) PublishTguiProfile(); }
    }
    private readonly Control _editorSettingsTab = new();
    private CharacterEditorAppearance _editorAppearance = new();
    private readonly Dictionary<string, string> _companyWebImages = new();
    private string? _catalogKey;
    private static readonly HashSet<string> CatalogFields = new()
    {
        "tabs", "speciesOptions", "sexOptions", "genderOptions", "spawnOptions", "hairOptions", "beardOptions",
        "markingOptions", "departments", "traits", "companyOptions"
    };
    private readonly Dictionary<Control, bool> _nativeTabVisibility = new();
    private Control? _nativeSpriteParent;
    private bool _clothesBeforeMarkings;

    private void InitializeTguiEditor()
    {
        _prototypeManager.PrototypesReloaded += ReloadTguiCatalog;
        _editorAppearance.Load(_resManager);
        TabContainer.AddChild(_editorSettingsTab);
        _profileWeb = new TguiPanel(suspendWhenHidden: true, inheritChatAppearance: false, showWindowControls: false, forceTgui: true) { Name = "CharacterTgui" };
        _profileWeb.OnAction += (action, payload) => HandleTguiProfileAction(action, payload);
        _personalLoadoutEditor.TguiMode = true;
        _nativeSpriteParent = SpriteView.Parent;
        // One browser owns every editor control and the shared animated background.
        // SpriteView remains the engine renderer, transparently above that background.
        for (var tab = 0; tab < TabContainer.ChildCount; tab++)
            foreach (var child in TabContainer.GetChild(tab).Children)
            {
                _nativeTabVisibility[child] = child.Visible;
                child.Visible = false;
            }
        IdentityNative.Visible = ProfileHighlight.Visible = ShowClothes.Visible = false;
        PreviewColumn.Visible = false;
        SpriteView.Orphan();
        SpriteView.MouseFilter = MouseFilterMode.Ignore;
        TabContainer.Orphan();
        TabContainer.Visible = false;
        _tguiHost = new TguiEditorHost(TabContainer, _profileWeb, SpriteView);
        EditorColumn.AddChild(_tguiHost);
        _profileWeb.SetNativeFallback(RestoreNativeEditor);
        _profileWeb.SetTguiRestore(RestoreTguiEditor);
        TabContainer.OnTabChanged += SelectTguiTab;
        SelectTguiTab(TabContainer.CurrentTab);
    }

    private void ReloadTguiCatalog(PrototypesReloadedEventArgs args)
    {
        _catalogKey = null;
        _selectionCatalogKey = null;
        _tguiImages.Reset();
        _personalLoadoutEditor.ResetTguiCache();
        _companyWebImages.Clear();
        UserInterfaceManager.DeferAction(() => { if (!Disposed) PublishTguiProfile(); });
    }

    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing)
    {
        if (disposing) _prototypeManager.PrototypesReloaded -= ReloadTguiCatalog;
        if (disposing) { CloseColorPicker(); CloseCatalog(); }
        if (disposing && _tguiHost is { Parent: null })
        {
            _tguiHost.Dispose();
        }
        base.Dispose(disposing);
    }

    private void RestoreNativeEditor()
    {
        if (_tguiHost?.Parent == null || _profileWeb == null) return;
        CloseCatalog();
        UpdateMarkings();
        UpdateHairPickers();
        UpdateCMarkingsHair();
        UpdateCMarkingsFacialHair();
        _profileWeb.Visible = false;
        TabContainer.Orphan();
        SpriteView.Orphan();
        _tguiHost.Orphan();
        EditorColumn.AddChild(TabContainer);
        _nativeSpriteParent?.AddChild(SpriteView);
        TabContainer.Visible = true;
        foreach (var (control, visible) in _nativeTabVisibility) control.Visible = visible;
        IdentityNative.Visible = ProfileHighlight.Visible = ShowClothes.Visible = PreviewColumn.Visible = true;
        _personalLoadoutEditor.TguiMode = false;
        TabContainer.OnTabChanged -= SelectTguiTab;
        InitializeNativeExtras();
    }

    private void RestoreTguiEditor()
    {
        if (_tguiHost == null || _profileWeb == null) return;
        TabContainer.Orphan();
        SpriteView.Orphan();
        for (var tab = 0; tab < TabContainer.ChildCount; tab++)
            foreach (var control in TabContainer.GetChild(tab).Children)
            {
                _nativeTabVisibility.TryAdd(control, control.Visible);
                control.Visible = false;
            }
        if (TabContainer.GetChild(TabContainer.CurrentTab) == _nativeBody?.Parent ||
            TabContainer.GetChild(TabContainer.CurrentTab) == _nativeTattoos?.Parent)
            TabContainer.CurrentTab = 0;
        IdentityNative.Visible = ProfileHighlight.Visible = ShowClothes.Visible = PreviewColumn.Visible = false;
        TabContainer.Visible = false;
        _tguiHost.AddChild(TabContainer);
        _tguiHost.AddChild(SpriteView);
        EditorColumn.AddChild(_tguiHost);
        _profileWeb.Visible = true;
        _personalLoadoutEditor.TguiMode = true;
        TabContainer.OnTabChanged += SelectTguiTab;
        _publishedTguiProfile = null;
        SelectTguiTab(TabContainer.CurrentTab);
    }

    private string TguiTabMode(int tab)
    {
        var child = TabContainer.GetChild(tab);
        if (child == _editorSettingsTab) return "settings";
        if (child == _personalLoadoutEditor) return "equipment";
        if (child == _savedItemsTab) return "saved";
        if (child == _flavorText) return "flavor";
        return tab switch { 0 => "appearance", 1 => "jobs", 2 => "traits", 3 => "company", _ => "markings" };
    }

    private void SelectTguiTab(int tab)
    {
        if (_profileWeb == null) return;
        CloseColorPicker();
        CloseCatalog();
        var wasMarkings = _tguiMode == "markings";
        _tguiMode = TguiTabMode(tab);
        if (_tguiMode == "markings" && !wasMarkings)
        {
            _clothesBeforeMarkings = ShowClothes.Pressed;
            ShowClothes.SetClickPressed(false);
            ReloadPreview();
        }
        else if (wasMarkings && _tguiMode != "markings")
        {
            ShowClothes.SetClickPressed(_clothesBeforeMarkings);
            ReloadPreview();
        }
        if (_tguiHost != null) _tguiHost.MarkingsLayout = _tguiMode == "markings";
        _profileWeb.Visible = _tguiMode != "native";
        _tguiHost?.InvalidateArrange();
        PublishTguiProfile();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (_tguiPublishPending)
        {
            _tguiPublishPending = false;
            FlushTguiProfile();
        }
        _tguiRefresh -= args.DeltaSeconds;
        if (_tguiRefresh > 0 || _profileWeb == null || !_profileWeb.VisibleInTree) return;
        _tguiRefresh = 0.2f;
        var bonus = _entManager.System<Content.Shared._DeepLagoon.DiscordLink.SharedDiscordBoostSystem>().GetLoadoutPointBonus(_playerManager.LocalSession);
        if (!ReferenceEquals(Profile, _publishedTguiProfile) || IsDirty != _publishedTguiDirty || ShowClothes.Pressed != _publishedTguiClothes || bonus != _publishedSupporterBonus || _requirements.TattoosBanned != _publishedTattoosBanned)
            PublishTguiProfile();
    }

    private void PublishTguiProfile() => _tguiPublishPending = true;

    private void FlushTguiProfile()
    {
        RefreshNativeExtras();
        PublishCatalog();
        if (_libraryWindow != null)
            _libraryWindow.Panel.SetState("CharacterHeadshotLibrary", CreateTguiProfileState("flavor").ToString());
        if (_profileWeb == null) return;
        _publishedTguiProfile = Profile;
        _publishedTguiDirty = IsDirty;
        _publishedTattoosBanned = _requirements.TattoosBanned;
        _publishedTguiClothes = ShowClothes.Pressed;
        _publishedSupporterBonus = _entManager.System<Content.Shared._DeepLagoon.DiscordLink.SharedDiscordBoostSystem>().GetLoadoutPointBonus(_playerManager.LocalSession);
        if (_tguiMode != "native")
        {
            var catalogKey = $"{_tguiMode}|{Profile?.Species.Id}|{Profile?.Sex}|{Profile?.UnrestrictedMarkings}|{TabContainer.ChildCount}|{_publishedSupporterBonus}|{IoCManager.Resolve<ILocalizationManager>().DefaultCulture?.Name}";
            if (catalogKey != _catalogKey)
            {
                _catalogKey = catalogKey;
                _profileWeb.SetStaticData(CreateTguiProfileState(_tguiMode).SelectFields(CatalogFields).ToString());
            }
            _profileWeb.SetIncrementalState("CharacterEditor", CreateTguiProfileState(_tguiMode, includeCatalogs: false));
        }
    }

    /// <summary>Current draft state; saving still uses LobbyUIController and server preferences.</summary>
    public TguiData CreateTguiProfileState(string mode, bool includeCatalogs = true)
    {
        var data = new TguiData().String("mode", mode).Bool("available", Profile != null)
            .Bool("maximized", EditorWindow?.Maximized == true).Bool("dirty", IsDirty).Bool("showClothes", ShowClothes.Pressed).Bool("libraryOpen", _libraryWindow != null);
        data.Bool("colorPickerOpen", _colorWindow != null).Object("colorPickerResult", _colorPickerResult)
            .Array("combinedMarkingCategories", _combineMarkings.Select(key => new TguiData().String("id", key)))
            .Bool("previewAroused", _previewGenitalAroused).Bool("canPreviewAroused", CanPreviewArousal);
        data.Object("appearance", _editorAppearance.Data());
        data.String("previewFloor", _tguiImages.Frame(new SpriteSpecifier.Texture(new ResPath("Tiles/steel.png"))));
        data.Bool("extendedHeadshot", _entManager.System<Content.Shared._DeepLagoon.DiscordLink.SharedDiscordBoostSystem>().GetSupporterTier(_playerManager.LocalSession) >= 2);
        if (SetupState != null) data.Object("setup", SetupState());
        if (Profile is not { } p) return data;
        data.Bool("canUndoAppearance", !_tattooFileBusy && _appearanceBaseline != null
                && (!SameAppearanceSelection(p.Appearance, _appearanceBaseline) || !_combineMarkings.SetEquals(_appearanceBaselineCombine)))
            .Bool("canRedoAppearance", !_tattooFileBusy && _appearanceRedo != null
                && (!SameAppearanceSelection(p.Appearance, _appearanceRedo) || !_combineMarkings.SetEquals(_appearanceRedoCombine)));
        if (mode == "appearance") data.Array("bodyFeatures", BodyCustomizationData());
        data.Bool("canUsePlantigradeLegs", _prototypeManager.Index(p.Species).PlantigradeSprites != null)
            .Bool("plantigradeLegs", p.Appearance.PlantigradeLegs);
        data.Array("previewSlots", _personalLoadoutEditor.CreateTguiSlots(PreviewDummy));
        if (includeCatalogs) data.Array("tabs", Enumerable.Range(0, TabContainer.ChildCount)
            .Where(i => TabContainer.GetChild(i) != _nativeBody?.Parent && TabContainer.GetChild(i) != _nativeTattoos?.Parent)
            .Select(i => new TguiData().Number("id", i)
            .String("name", Loc.GetString("dl-character-editor-tab-" + TguiTabMode(i)))
            .String("mode", TguiTabMode(i))));
        if (mode == "saved") data.Array("savedItems", _savedItemEntities.Where(_entManager.EntityExists).Select(entity =>
            new TguiData().String("name", _entManager.GetComponent<MetaDataComponent>(entity).EntityName)
                .Array("images", _tguiImages.EntityImages(entity))));
        data.String("oocNotes", p.OocNotes).Bool("hasHeadshot", p.HeadshotId.Length > 0).String("flavorText", p.FlavorText).Number("maxFlavorLength", HumanoidCharacterProfile.MaxDescLength);
        if (mode == "flavor" && CharacterSlot is { } headshotSlot)
        {
            EnsureHeadshotPreview(headshotSlot, p.HeadshotId);
            var preview = _headshotPreviews[headshotSlot];
            data.String("headshot", preview.Image).String("headshotStatus", preview.Status);
            data.Object("gallery", GalleryState(headshotSlot));
        }
        if (mode == "equipment") data.Object("equipment", _personalLoadoutEditor.CreateTguiState());
        if (mode == "markings")
        {
            data.Object("tattooEditor", _tattooEditor.Build(PreviewDummy, p.Appearance.Tattoos, _tattooStatus).Bool("fileBusy", _tattooFileBusy));
            data.Array("markings", MarkingSelectionData());
            if (includeCatalogs) data.Array("markingOptions", MarkingOptionData());
        }
        data.Bool("traitsValid", TraitSelection.Evaluate(p, _prototypeManager, _cfgManager, TraitSelection.PreferredJob(p)).Valid);
        var species = _prototypeManager.Index(p.Species);
        if (mode == "appearance") data.String("hairPreviewImage", HairPreviewImage());
        data.String("name", p.Name).Number("age", p.Age).String("species", p.Species.Id)
            .String("speciesName", Loc.GetString(species.Name))
            .Bool("markingSpeciesRestricted", !p.UnrestrictedMarkings && p.Species.Id != "Synth")
            .Number("sex", (int)p.Sex).Number("gender", (int)p.Gender)
            .Number("spawn", (int)p.SpawnPriority).Number("height", p.Appearance.Height).Number("width", p.Appearance.Width)
            .String("hairStyle", p.Appearance.HairStyleId).String("beardStyle", p.Appearance.FacialHairStyleId)
            .String("hairColor", p.Appearance.HairColor.ToHexNoAlpha()).String("beardColor", p.Appearance.FacialHairColor.ToHexNoAlpha())
            .String("eyeColor", p.Appearance.EyeColor.ToHexNoAlpha()).String("skinColor", p.Appearance.SkinColor.ToHexNoAlpha())
            .Bool("hairLocked", _markingManager.MustMatchSkin(p.Species, HumanoidVisualLayers.Hair, out _, _prototypeManager))
            .Bool("beardLocked", _markingManager.MustMatchSkin(p.Species, HumanoidVisualLayers.FacialHair, out _, _prototypeManager))
            .Number("skinTone", Skin.Value).Bool("humanSkin", species.SkinColoration == HumanoidSkinColor.HumanToned)
            .Number("minAge", species.MinAge).Number("maxAge", species.MaxAge)
            .Number("erp", (int)p.ERPConsent).Number("noncon", (int)p.NonConConsent).Number("vore", (int)p.VoreConsent)
            .Array("physiology", Content.Client._DeepLagoon.InteractionPanel.BlueMoonPreferencesData.Options(p.ArousalPreferences))
            .Number("unavailable", (int)p.PreferenceUnavailable).String("company", p.Company);
        if (mode == "appearance" && includeCatalogs)
        {
            data.Array("speciesOptions", _species.Select(s => Choice(s.ID, Loc.GetString(s.Name))))
                .Array("sexOptions", species.Sexes.Select(s => Choice(((int)s).ToString(), Loc.GetString($"humanoid-profile-editor-sex-{s.ToString().ToLowerInvariant()}-text"))))
                .Array("genderOptions", Enum.GetValues<Gender>().Select(g => Choice(((int)g).ToString(), Loc.GetString($"humanoid-profile-editor-pronouns-{g.ToString().ToLowerInvariant()}-text"))))
                .Array("spawnOptions", Enum.GetValues<SpawnPriorityPreference>().Select(s => Choice(((int)s).ToString(), Loc.GetString($"humanoid-profile-editor-preference-spawn-priority-{s.ToString().ToLowerInvariant()}"))))
                .Array("hairOptions", HairChoices(false)).Array("beardOptions", HairChoices(true));
        }
        else if (mode == "jobs")
        {
            if (includeCatalogs) data.Array("departments", _prototypeManager.EnumeratePrototypes<DepartmentPrototype>().Where(d => !d.EditorHidden)
                .OrderBy(d => Loc.GetString(d.Name)).Select(d => new TguiData().String("id", d.ID).String("name", Loc.GetString(d.Name))
                    .Array("jobs", d.Roles.Select(id => _prototypeManager.Index(id)).Where(j => j.SetPreference)
                        .OrderBy(j => j.LocalizedName).Select(JobData))));
            else
            {
                var jobs = new TguiData();
                foreach (var job in _prototypeManager.EnumeratePrototypes<JobPrototype>().Where(job => job.SetPreference))
                    jobs.Object(job.ID, JobDynamicData(job));
                data.Object("jobState", jobs);
            }
        }
        else if (mode == "traits")
        {
            var status = TraitSelection.Evaluate(p, _prototypeManager, _cfgManager, TraitSelection.PreferredJob(p));
            if (includeCatalogs) data.Array("traits", _prototypeManager.EnumeratePrototypes<TraitPrototype>()
                .Where(t => !t.Disabled).OrderBy(t => Loc.GetString(t.Name)).Select(t =>
                    TguiTraitState(t).String("id", t.ID).String("name", Loc.GetString(t.Name))
                        .String("description", t.Description is { } description ? Loc.GetString(description) : "")
                        .String("category", t.Category is { } category && _prototypeManager.TryIndex(category, out var cat)
                            ? Loc.GetString(cat.Name) : Loc.GetString("humanoid-profile-editor-traits-default-category"))
                        .Number("points", t.Points).Number("slots", t.Slots)));
            else
            {
                var traits = new TguiData();
                foreach (var trait in _prototypeManager.EnumeratePrototypes<TraitPrototype>().Where(t => !t.Disabled))
                    traits.Object(trait.ID, TguiTraitState(trait));
                data.Object("traitState", traits);
            }
            data.Object("traitBudget", new TguiData().Number("points", status.Points).Number("slots", status.Slots)
                .Number("maxSlots", status.MaxSlots).Bool("valid", status.Valid)
                .Array("reasons", builder => builder.Append(string.Join(',', status.Reasons.Select(GameWebView.Quote)))));
        }
        else if (mode == "company")
        {
            var companies = _prototypeManager.EnumeratePrototypes<CompanyPrototype>().Where(c => !c.Disabled && _companyManager.IsAllowed(c.ID));
            if (includeCatalogs) data.Array("companyOptions", companies.OrderBy(c => c.ID == "None" ? "" : c.Name).Select(c => Choice(c.ID, c.Name)
                .String("descriptionHtml", WebChatMessageFormatter.ToHtml(FormattedMessage.FromMarkupPermissive(string.IsNullOrEmpty(c.Description) ? "" : Loc.GetString(c.Description))))
                .String("image", CompanyDataImage(c)).String("wikiUrl", IoCManager.Resolve<WikiRoutingConfiguration>().Read().Resolve(new[] { "company:" + c.ID }, WikiRoutingConfiguration.External(c.WikiUrl)))));
            if (_prototypeManager.TryIndex<CompanyPrototype>(p.Company, out var company))
                data.String("companyDescriptionHtml", WebChatMessageFormatter.ToHtml(FormattedMessage.FromMarkupPermissive(
                        string.IsNullOrEmpty(company.Description) ? "" : Loc.GetString(company.Description))))
                    .String("companyImage", CompanyDataImage(company));
        }
        return data;
    }

    private static TguiData Choice(string id, string name) => new TguiData().String("id", id).String("name", name);

    private void EnsureHeadshotPreview(int slot, string id)
    {
        if (_headshotPreviews.TryGetValue(slot, out var cached) && cached.Id == id)
            return;

        _headshotPreviews[slot] = (id, "", id.Length > 0 ? "Загрузка предпросмотра…" : "");
        if (id.Length == 0)
        {
            _headshotReads.Remove(slot);
            return;
        }

        var request = Guid.NewGuid();
        _headshotReads[slot] = request;
        _entManager.System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>().Read(slot, null, (bytes, mime, returnedId, error) =>
        {
            if (Disposed || !_headshotReads.TryGetValue(slot, out var currentRequest) || currentRequest != request)
                return;

            // Keep this generation while the image is selected: Read can first
            // return a local preview, then deliver a replacement after hash validation.
            _headshotPreviews[slot] = (id,
                error.Length == 0 && bytes.Length > 0 && returnedId == id ? HeadshotImage(bytes, mime) : "",
                error);
            if (CharacterSlot == slot)
                PublishTguiProfile();
        });
    }

    private static string HeadshotImage(byte[] bytes, string mime) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    private IEnumerable<TguiData> HairChoices(bool beard)
    {
        if (Profile == null) yield break;
        yield return Choice(beard ? HairStyles.DefaultFacialHairStyle : HairStyles.DefaultHairStyle, beard ? "Без бороды" : "Без волос");
        var category = beard ? MarkingCategories.FacialHair : MarkingCategories.Hair;
        foreach (var marking in _markingManager.MarkingsByCategoryAndSpecies(category, Profile.Species, Profile.UnrestrictedMarkings).Values
                     .Where(m => _markingManager.CanBeApplied(Profile.Species, Profile.Sex, m, _prototypeManager, Profile.UnrestrictedMarkings))
                     .OrderBy(m => Loc.GetString($"marking-{m.ID}")))
            if (marking.ID != (beard ? HairStyles.DefaultFacialHairStyle : HairStyles.DefaultHairStyle))
                yield return Choice(marking.ID, Loc.GetString($"marking-{marking.ID}")).String("iconId", "marking/" + marking.ID);
    }

    private TguiData JobData(JobPrototype job)
    {
        return JobDynamicData(job).String("id", job.ID).String("name", job.LocalizedName).String("description", job.LocalizedDescription);
    }

    private TguiData JobDynamicData(JobPrototype job)
    {
        var allowed = _requirements.IsAllowed(job, (HumanoidCharacterProfile?)_preferencesManager.Preferences?.SelectedCharacter, out var reason);
        return new TguiData().Bool("allowed", allowed).String("reason", reason?.ToString() ?? "")
            .Number("priority", (int)(Profile?.JobPriorities.GetValueOrDefault(job.ID, JobPriority.Never) ?? JobPriority.Never));
    }

    private TguiData TguiTraitState(TraitPrototype trait)
    {
        if (Profile == null) return new TguiData();
        var selected = Profile.TraitPreferences.Contains(trait.ID);
        var candidate = selected ? Profile : Profile.WithTraitPreference(trait.ID, _prototypeManager);
        var available = TraitSelection.IsAvailable(trait, candidate, candidate.TraitPreferences,
            _prototypeManager, _cfgManager, TraitSelection.PreferredJob(candidate), out var reason);
        var status = TraitSelection.Evaluate(candidate, _prototypeManager, _cfgManager, TraitSelection.PreferredJob(candidate));
        return new TguiData().Bool("selected", selected).Bool("available", available).Bool("visible", true)
            .Bool("canSelect", selected || available && status.Valid).String("reason", reason.Length > 0 ? reason
                : !status.Valid ? string.Join("\n", status.Reasons) : "");
    }

    private bool CanSelectTguiTrait(TraitPrototype trait)
    {
        if (Profile == null) return false;
        var candidate = Profile.WithTraitPreference(trait.ID, _prototypeManager);
        return TraitSelection.Evaluate(candidate, _prototypeManager, _cfgManager, TraitSelection.PreferredJob(candidate)).Valid;
    }

    private string CompanyDataImage(CompanyPrototype company)
    {
        if (string.IsNullOrEmpty(company.Image)) return "";
        if (_companyWebImages.TryGetValue(company.ID, out var cached)) return cached;
        var image = "";
        try
        {
            using var stream = _resManager.ContentFileRead(new ResPath(company.Image));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (buffer.Length <= 512 * 1024 && company.Image.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                image = "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
        }
        catch (Exception) { /* Match native UI's optional company image. */ }
        _companyWebImages[company.ID] = image;
        return image;
    }

    /// <summary>All web actions edit the same draft and use the existing save/import/export handlers.</summary>
    public bool HandleTguiProfileAction(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var args)) return false;
        if (action == "window-maximize")
        {
            if (EditorWindow is not { } window || !bool.TryParse(args!.String("value"), out var maximized)) return false;
            if (window.Maximized != maximized) window.ToggleMaximize();
            PublishTguiProfile(); return true;
        }
        if (_requirements.TattoosBanned && action.StartsWith("tattoo-", StringComparison.Ordinal) && action != "tattoo-preview-direction")
        { _tattooStatus = Content.Shared._DeepLagoon.Tattoos.TattooAccess.BanMessage; PublishTguiProfile(); return false; }
        if (action == "catalog-open") return OpenCatalog(args!.String("kind") ?? "");
        if (action == "catalog-close") { CloseCatalog(); return true; }
        if (action == "preview-occluded")
        {
            _tguiHost?.SetPreviewOccluded(args!.String("value") == "true");
            return true;
        }
        if (action.StartsWith("color-picker-", StringComparison.Ordinal)) return HandleColorPicker(action, args!);
        if (action == "preview-bounds")
        {
            if (_tguiMode != "markings" || !args!.TryFloat("x", out var x) || !args.TryFloat("y", out var y)
                || !args.TryFloat("width", out var width) || !args.TryFloat("height", out var height)
                || width <= 0 || height <= 0) return false;
            _tguiHost?.SetPreviewBounds(new System.Numerics.Vector2(x, y), new System.Numerics.Vector2(width, height));
            return true;
        }
        if (action.StartsWith("tattoo-", StringComparison.Ordinal)
            && action != "tattoo-preview-direction" && action != "tattoo-remove" && action != "tattoo-clear" && action != "tattoo-export"
            && !_cfgManager.GetCVar(Content.Shared._DeepLagoon.Tattoos.TattooCVars.Enabled))
        { _tattooStatus = "Нанесение татуировок выключено в настройках."; PublishTguiProfile(); return false; }
        if (action is "tattoo-export" or "tattoo-import")
        {
            if (_tattooFileBusy || Profile?.Appearance.Tattoos.FirstOrDefault(t => t.Id == args!.String("id")) is not { } selectedTattoo) return false;
            HandleTattooFileAction(action, selectedTattoo.Clone()); return true;
        }
        if (_tattooFileBusy && action.StartsWith("tattoo-", StringComparison.Ordinal)) return false;
        if (action == "tattoo-bake-chunk")
        {
            if (!_tattooEditor.ReceiveBakeChunk(args!, out var completed)) return false;
            if (completed == null) return true;
            args = completed; action = "tattoo-bake";
        }
        if (action == "icons") return _profileWeb != null && SendTguiIcons(_profileWeb, args!);
        if (action.StartsWith("setup/", StringComparison.Ordinal)) { var accepted = SetupAction?.Invoke(action[6..], args!) ?? false; PublishTguiProfile(); return accepted; }
        if (Profile == null) return false;
        var value = args!.String("value") ?? "";
        var species = _prototypeManager.Index(Profile.Species);
        var valid = true;
        if (action.StartsWith("tattoo-", StringComparison.Ordinal))
        {
            if (action == "tattoo-bake") { HandleTattooBake(args!); return true; }
            if (action == "tattoo-preview-direction" && args!.TryInt("direction", out var tattooDirection) && tattooDirection is >= 0 and < 4)
            {
                _previewRotation = tattooDirection switch { 1 => Direction.North, 2 => Direction.East, 3 => Direction.West, _ => Direction.South };
                SetPreviewRotation(_previewRotation);
                return true;
            }
            if (Content.Client._DeepLagoon.Tattoos.TattooEditorData.TryApply(action, args!, Profile.Appearance.Tattoos, out var tattoos, out _tattooStatus))
            {
                var appearance = Profile.Appearance.Clone();
                appearance.Tattoos = tattoos;
                _appearanceRedo = null;
                Profile = Profile.WithCharacterAppearance(appearance);
                ReloadProfilePreview();
                SetDirty();
            }
            PublishTguiProfile();
            return true;
        }
        if (action.StartsWith("equipment/", StringComparison.Ordinal))
        {
            var result = _personalLoadoutEditor.HandleTguiAction(action[10..], args);
            PublishTguiProfile();
            return result;
        }
        switch (action)
        {
            case "appearance-undo":
            case "appearance-redo":
                if (!RestoreAppearanceSelection(action == "appearance-redo")) return false;
                break;
            case "plantigrade-legs":
                if (!bool.TryParse(value, out var straightLegs) || !SetPlantigradeLegs(straightLegs)) return false;
                break;
            case "body-feature": return HandleBodyCustomizationAction(args);
            case "headshot-library":
                ToggleHeadshotLibrary(); break;
            case "headshot-library-close":
                CloseHeadshotLibrary(); break;
            case "company-wiki":
                if (!_prototypeManager.TryIndex<CompanyPrototype>(value, out var wikiCompany) || !_companyManager.IsAllowed(wikiCompany.ID) ||
                    wikiCompany.Disabled) return false;
                UserInterfaceManager.GetUIController<GuidebookUIController>().OpenSource("company:" + wikiCompany.ID, fallback: WikiRoutingConfiguration.External(wikiCompany.WikiUrl)); break;
            case "appearance-setting":
                if (!_editorAppearance.Update(args.String("field") ?? "", value)) return false;
                _editorAppearance.Save(_resManager); break;
            case "appearance-reset":
                _editorAppearance = new CharacterEditorAppearance();
                _editorAppearance.Save(_resManager); break;
            case "select-tab":
                if (!args.TryInt("value", out var selectedTab) || selectedTab < 0 || selectedTab >= TabContainer.ChildCount) return false;
                TabContainer.CurrentTab = selectedTab; break;
            case "character-card":
                Content.Client._DeepLagoon.CharacterInfo.CharacterCardWindow.Open(PreviewDummy, Profile.Name, Profile.FlavorText, Profile.OocNotes, (byte)Profile.ERPConsent, (byte)Profile.NonConConsent, (byte)Profile.VoreConsent, CharacterSlot ?? -1); break;
            case "headshot-gallery":
                if (CharacterSlot is not { } gallerySlot || args.String("operation") is not { } operation || operation is not ("toggle" or "primary" or "delete")) return false;
                RefreshGallery(gallerySlot, operation, value); break;
            case "headshot-upload":
            case "headshot-add":
                if (CharacterSlot is not { } slot) return false;
                EnsureHeadshotPreview(slot, Profile.HeadshotId);
                var existingPreview = _headshotPreviews[slot];
                _headshotPreviews[slot] = (existingPreview.Id, existingPreview.Image, "Загрузка…");
                _entManager.System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>().Upload(slot, (bytes, mime, id, error) =>
                {
                    if (Disposed) return;
                    if (error.Length == 0 && id.Length > 0) RefreshGallery(slot);
                    var status = error.Length > 0 ? error : id.Length > 0 ? "Изображение сохранено на сервере." : "Загрузка отменена.";
                    if (error.Length == 0 && id.Length > 0) _headshotReads.Remove(slot);
                    _headshotPreviews[slot] = error.Length == 0 && id.Length > 0
                        ? (id, HeadshotImage(bytes, mime), status)
                        : (existingPreview.Id, existingPreview.Image, status);
                    if (CharacterSlot == slot)
                    {
                        if (Profile != null && id.Length > 0) Profile = Profile.WithHeadshotId(id);
                        PublishTguiProfile();
                    }
                }, action == "headshot-add", value); break;
            case "headshot-download":
                if (CharacterSlot is not { } downloadSlot) return false;
                _entManager.System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>().Download(downloadSlot); break;
            case "ooc":
                if (value.Length > HumanoidCharacterProfile.MaxDescLength) return false;
                Profile = Profile.WithOocNotes(value); SetDirty(); break;
            case "flavor":
                if (value.Length > HumanoidCharacterProfile.MaxDescLength) return false;
                OnFlavorTextChange(value); break;
            case "marking-toggle":
            case "marking-add":
            case "marking-remove":
            case "marking-color":
            case "marking-move":
            case "marking-side":
            case "marking-glow":
                if (!HandleTguiMarking(action, args, value)) return false;
                break;
#if DEBUG
            case "dev-select-tab":
                if (!args.TryInt("value", out var tab) || tab < 0 || tab >= TabContainer.ChildCount) return false;
                TabContainer.CurrentTab = tab; break;
#endif
            case "name": if (value.Length > HumanoidCharacterProfile.MaxNameLength) return false; SetName(value); break;
            case "random-name": RandomizeName(); break;
            case "random-all": RandomizeEverything(); break;
            case "save": if (!IsDirty || !TraitSelection.Evaluate(Profile, _prototypeManager, _cfgManager, TraitSelection.PreferredJob(Profile)).Valid) return false; Save?.Invoke(); break;
            case "reset": ResetToDefault(); break;
            case "import": ImportProfile(); break;
            case "export": ExportProfile(); break;
            case "export-image": ExportImage(); break;
            case "open-images": _resManager.UserData.OpenOsWindow(Content.Client.Sprite.ContentSpriteSystem.Exports); break;
            case "marking-combine":
                if (!_markingManager.Markings.Values.Any(option => MarkingCategoryKey(option) == value)) return false;
                _appearanceRedo = null;
                if (!_combineMarkings.Add(value))
                {
                    _combineMarkings.Remove(value);
                    DisableMarkingCombination(value);
                }
                break;
            case "preview-arousal":
                if (!CanPreviewArousal) return false;
                _previewGenitalAroused = !_previewGenitalAroused;
                ApplyPreviewArousal();
                break;
            case "clothes": if (_tguiMode == "markings") return false; ShowClothes.SetClickPressed(!ShowClothes.Pressed); ReloadPreview(); break;
            case "rotate":
                if (!args.TryInt("value", out var turn) || turn is not (-1 or 1)) return false;
                _previewRotation = (Direction)(((int)_previewRotation + (turn < 0 ? 6 : 2)) % 8);
                SetPreviewRotation(_previewRotation); break;
            case "preview-slot":
                if (!_personalLoadoutEditor.SelectTguiSlot(PreviewDummy, value)) return false;
                break;
            case "preview-slot-remove":
                if (!_personalLoadoutEditor.RemoveTguiSlot(PreviewDummy, value)) return false;
                break;
            case "species":
                if (!_species.Any(s => s.ID == value)) return false;
                SetSpecies(value); UpdateAgeEdit(); UpdateSkinColor(); break;
            case "age":
                if (!args.TryInt("value", out var age) || age < species.MinAge || age > species.MaxAge) return false;
                SetAge(age); UpdateAgeEdit(); break;
            case "sex":
                if (!args.TryInt("value", out var sex) || !species.Sexes.Contains((Sex)sex)) return false;
                SetSex((Sex)sex); UpdateSexControls(); break;
            case "gender":
                if (!args.TryInt("value", out var gender) || !Enum.IsDefined((Gender)gender)) return false;
                SetGender((Gender)gender); UpdateGenderControls(); break;
            case "spawn":
                if (!args.TryInt("value", out var spawn) || !Enum.IsDefined((SpawnPriorityPreference)spawn)) return false;
                SetSpawnPriority((SpawnPriorityPreference)spawn); UpdateSpawnPriorityControls(); break;
            case "height":
            case "width":
                if (!args.TryFloat("value", out var size) || size < 0.8f || size > 1.2f) return false;
                if (action == "height") { SetHeight(size); UpdateHeightControls(); } else { SetWidth(size); UpdateWidthControls(); }
                break;
            case "hair-style":
            case "beard-style":
                var beard = action == "beard-style";
                var category = beard ? MarkingCategories.FacialHair : MarkingCategories.Hair;
                var empty = beard ? HairStyles.DefaultFacialHairStyle : HairStyles.DefaultHairStyle;
                if (value != empty && (!_markingManager.Markings.TryGetValue(value, out var marking) || marking.MarkingCategory != category ||
                    !_markingManager.CanBeApplied(Profile.Species, Profile.Sex, marking, _prototypeManager, Profile.UnrestrictedMarkings))) return false;
                Profile = Profile.WithCharacterAppearance(beard ? Profile.Appearance.WithFacialHairStyleName(value) : Profile.Appearance.WithHairStyleName(value));
                ReloadProfilePreview(); break;
            case "hair-color":
            case "beard-color":
            case "eye-color":
            case "skin-color":
                if (value.Length != 7 || value[0] != '#') return false;
                Color color;
                try { color = Color.FromHex(value); } catch (Exception) { return false; }
                if (action == "hair-color" || action == "beard-color")
                {
                    var layer = action == "hair-color" ? HumanoidVisualLayers.Hair : HumanoidVisualLayers.FacialHair;
                    if (_markingManager.MustMatchSkin(Profile.Species, layer, out _, _prototypeManager)) return false;
                    Profile = Profile.WithCharacterAppearance(action == "hair-color" ? Profile.Appearance.WithHairColor(color) : Profile.Appearance.WithFacialHairColor(color));
                    ReloadProfilePreview();
                }
                else if (action == "eye-color")
                {
                    Profile = Profile.WithCharacterAppearance(Profile.Appearance.WithEyeColor(color)); UpdateEyePickers(); ReloadProfilePreview();
                }
                else
                {
                    if (species.SkinColoration == HumanoidSkinColor.HumanToned)
                        Skin.Value = SkinColor.HumanSkinToneFromColor(color);
                    else _rgbSkinColorSelector.Color = color;
                    OnSkinColorOnValueChanged();
                }
                break;
            case "skin-tone":
                if (species.SkinColoration != HumanoidSkinColor.HumanToned || !args.TryFloat("value", out var tone) || tone < 0 || tone > 100) return false;
                Skin.Value = tone; OnSkinColorOnValueChanged(); break;
            case "erp":
            case "noncon":
            case "vore":
                if (!args.TryInt("value", out var consent) || !Enum.IsDefined((InteractionPanelConsent)consent)) return false;
                var consentCategory = action == "erp" ? InteractionPanelCategory.Erotic : action == "noncon" ? InteractionPanelCategory.NonCon : InteractionPanelCategory.Vore;
                Profile = Profile.WithInteractionPanelConsent(consentCategory, (InteractionPanelConsent)consent); UpdateInteractionPanelPreferences(); SetDirty(); break;
            case "physiology":
                if (args.String("id") is not { } physiologyId || !args.TryFloat("value", out var physiologyValue) || !float.IsFinite(physiologyValue)) return false;
                Profile = Profile.WithArousalPreferences(Profile.ArousalPreferences.WithOption(physiologyId, physiologyValue));
                SetDirty(); break;
            case "job":
                if (args.String("id") is not { } jobId || !_prototypeManager.TryIndex<JobPrototype>(jobId, out var job) || !job.SetPreference ||
                    !_requirements.IsAllowed(job, (HumanoidCharacterProfile?)_preferencesManager.Preferences?.SelectedCharacter, out _) ||
                    !args.TryInt("value", out var priority) || !Enum.IsDefined((JobPriority)priority)) return false;
                if ((JobPriority)priority == JobPriority.High)
                    foreach (var (id, previous) in Profile.JobPriorities.ToArray())
                        if (id != jobId && previous == JobPriority.High) Profile = Profile.WithJobPriority(id, JobPriority.Medium);
                Profile = Profile.WithJobPriority(jobId, (JobPriority)priority); UpdateJobPriorities(); ReloadPreview(); break;
            case "unavailable":
                if (!args.TryInt("value", out var unavailable) || !Enum.IsDefined((PreferenceUnavailableMode)unavailable)) return false;
                Profile = Profile.WithPreferenceUnavailable((PreferenceUnavailableMode)unavailable); PreferenceUnavailableButton.SelectId(unavailable); SetDirty(); break;
            case "trait":
                if (args.String("id") is not { } traitId || !_prototypeManager.TryIndex<TraitPrototype>(traitId, out var trait)) return false;
                if (Profile.TraitPreferences.Contains(trait.ID)) Profile = Profile.WithoutTraitPreference(trait.ID, _prototypeManager);
                else { if (!CanSelectTguiTrait(trait)) return false; Profile = Profile.WithTraitPreference(trait.ID, _prototypeManager); }
                RefreshTraits(); RefreshAppearanceAccess(); SetDirty(); break;
            case "clear-traits": Profile = Profile.WithoutAllTraitPreferences(); RefreshTraits(); RefreshAppearanceAccess(); SetDirty(); break;
            case "company":
                if (!_prototypeManager.TryIndex<CompanyPrototype>(value, out var company) || company.Disabled || !_companyManager.IsAllowed(company.ID)) return false;
                Profile = Profile.WithCompany(value); UpdateCompanyControls(); SetDirty(); break;
            case "species-guide":
                OnSpeciesInfoButtonPressed(null!); break;
            case "job-guide":
                if (args.String("id") is not { } guideJob || !_prototypeManager.TryIndex<JobPrototype>(guideJob, out var guideProto)) return false;
                UserInterfaceManager.GetUIController<GuidebookUIController>().OpenSource("job:" + guideProto.ID, guideProto.Guides?.Select(id => id.ToString())); break;
            default: valid = false; break;
        }
        if (valid) PublishTguiProfile();
        return valid;
    }

    private bool HandleTguiMarking(string action, TguiActionData args, string value)
    {
        if (Profile == null) return false;
        if (action != "marking-color") CloseColorPicker();
        var list = Profile.Appearance.Markings.Select(m => new Marking(m)).ToList();
        var points = _prototypeManager.Index(Profile.Species).MarkingPoints;
        if (action is "marking-add" or "marking-toggle")
        {
            CloseColorPicker();
            if (!_markingManager.Markings.TryGetValue(value, out var prototype) || prototype.MarkingCategory is MarkingCategories.Hair or MarkingCategories.FacialHair ||
                prototype.EditorHidden || !_markingManager.CanBeApplied(Profile.Species, Profile.Sex, prototype, _prototypeManager, Profile.UnrestrictedMarkings)) return false;
            if (action == "marking-toggle" && SelectedPair(list, prototype).FirstOrDefault() is { } current)
            {
                list.RemoveAll(member => PairParts(prototype).Any(part => part.ID == member.MarkingId));
            }
            else if (!TrySelectTguiMarking(list, prototype, points)) return false;
        }
        else
        {
            if (!args.TryInt("index", out var index) || index < 0 || index >= list.Count) return false;
            var marking = list[index];
            if (action == "marking-remove")
            {
                if (!_markingManager.TryGetMarking(marking, out var removed)) return false;
                list.RemoveAll(member => PairParts(removed).Any(part => part.ID == member.MarkingId));
            }
            else if (action == "marking-side")
            {
                if (value is not ("left" or "right" or "both") || !_markingManager.TryGetMarking(marking, out var definition)
                    || _markingManager.EditorMirror(definition) == null) return false;
                list.RemoveAll(member => PairParts(definition).Any(part => part.ID == member.MarkingId));
                if (!TrySelectTguiMarking(list, CanonicalMarking(definition), points, value, marking)) return false;
            }
            else if (action == "marking-glow")
            {
                if (!CanUseMarkingGlow || !_markingManager.TryGetMarking(marking, out var definition) || !definition.CanGlow) return false;
                var glow = !marking.Unshaded;
                foreach (var member in SelectedPair(list, definition)) member.Unshaded = glow;
            }
            else if (action == "marking-move")
            {
                if (!args.TryInt("value", out var direction) || direction is not (-1 or 1)
                    || !MoveTguiMarking(list, marking, direction)) return false;
            }
            else
            {
                if (!_markingManager.TryGetMarking(marking, out var prototype) || prototype.ForcedColoring || _markingManager.MustMatchSkin(Profile.Species, prototype.BodyPart, out _, _prototypeManager) ||
                    !args.TryInt("layer", out var layer) || layer < 0 || layer >= marking.MarkingColors.Count || !Color.TryFromHex(value, out var color)) return false;
                marking.SetColor(layer, color);
                foreach (var member in SelectedPair(list, prototype))
                    if (layer < member.MarkingColors.Count) member.SetColor(layer, color);
            }
        }
        RestoreTguiBodyMarkings(list, points);
        var updated = new MarkingSet(list, points, _markingManager, _prototypeManager, Profile.UnrestrictedMarkings);
        updated.EnsureSpecies(Profile.Species, Profile.Appearance.SkinColor, _markingManager, _prototypeManager, Profile.UnrestrictedMarkings);
        updated.EnsureSexes(Profile.Sex, _markingManager);
        OnMarkingChange(updated);
        return true;
    }

    private bool SendTguiIcons(TguiPanel target, TguiActionData args)
    {
        if (args.String("ids") is not { } ids || Profile == null) return false;
        var icons = new List<TguiData>();
        foreach (var id in ids.Split(',').Distinct().Take(100))
        {
            if (id.StartsWith("marking/", StringComparison.Ordinal) &&
                _markingManager.Markings.TryGetValue(id[8..], out var marking))
                icons.Add(new TguiData().String("id", id).Array("layers", marking.Sprites.Select(sprite =>
                    new TguiData().String("url", _tguiImages.Frame(sprite)).String("color", "#ffffff"))));
            else if (id.StartsWith("item/", StringComparison.Ordinal) && _prototypeManager.TryIndex<EntityPrototype>(id[5..], out _))
                icons.Add(new TguiData().String("id", id).Array("layers", _tguiImages.Item(id[5..])));
            else
                icons.Add(new TguiData().String("id", id).Array("layers", Enumerable.Empty<TguiData>()));
        }
        target.Web.Send("update", new TguiData().Object("data", new TguiData().Object("iconBatch", new TguiData().Array("icons", icons))).ToString());
        return true;
    }

}
