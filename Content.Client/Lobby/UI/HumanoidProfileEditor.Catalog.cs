using System.Numerics;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.IoC;
using Robust.Shared.Localization;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private CharacterAuxiliaryWindow? _catalogWindow;
    private string _catalogKind = "";
    private int? _catalogSlot;
    private string? _selectionCatalogKey;

    private IEnumerable<TguiData> MarkingRaceChoices(MarkingPrototype marking)
    {
        var species = _markingManager.GetSpeciesCategories(marking);
        if (species == null)
        {
            yield return Choice("common", Loc.GetString("markings-category-common"));
            yield break;
        }
        if (species.Count == 0)
        {
            yield return Choice("genetic", Loc.GetString("trait-genetic-modification-name"));
            yield break;
        }
        foreach (var id in species.Order())
            yield return Choice(id, _prototypeManager.TryIndex<SpeciesPrototype>(id, out var prototype)
                ? Loc.GetString(prototype.Name) : id);
    }

    private void CloseCatalog()
    {
        var window = _catalogWindow;
        _catalogWindow = null;
        _catalogSlot = null;
        _selectionCatalogKey = null;
        _catalogKind = "";
        window?.Close();
        window?.Dispose();
    }

    private bool OpenCatalog(string kind)
    {
        if (Profile == null || kind is not ("hair" or "beard" or "markings")
            || _tguiMode != (kind == "markings" ? "markings" : "appearance")) return false;
        _catalogKind = kind;
        _catalogSlot = CharacterSlot;
        if (_catalogWindow == null)
        {
            var window = new CharacterAuxiliaryWindow { SetSize = new Vector2(760, 600) };
            _catalogWindow = window;
            window.Panel.SetNativeFallback(() => _profileWeb?.SwitchToNative());
            window.Panel.OnAction += (action, payload) =>
            {
                if (_catalogWindow != window || _catalogSlot != CharacterSlot || Profile == null) return;
                if (action == "icons")
                {
                    if (TguiActionData.TryParse(payload, out var args)) SendTguiIcons(window.Panel, args!);
                    return;
                }
                if (action == "catalog-close" || (_catalogKind, action) is
                    ("hair", "hair-style") or ("beard", "beard-style") or ("markings", "marking-toggle") or ("markings", "marking-combine"))
                    HandleTguiProfileAction(action, payload);
            };
            window.Panel.OnClose += CloseCatalog;
            window.OnClose += () =>
            {
                if (_catalogWindow != window) return;
                _catalogWindow = null;
                _catalogSlot = null;
                _selectionCatalogKey = null;
                _catalogKind = "";
                window.Dispose();
            };
            window.OpenCentered();
        }
        PublishCatalog();
        _catalogWindow?.MoveToFront();
        return true;
    }

    public TguiData CreateTguiCatalogState(bool includeCatalogs = true) =>
        CreateTguiProfileState(_catalogKind == "markings" ? "markings" : "appearance", includeCatalogs)
            .String("kind", _catalogKind);

    private void PublishCatalog()
    {
        if (_catalogWindow == null) return;
        if (Profile == null || _catalogSlot != CharacterSlot) { CloseCatalog(); return; }
        var key = $"{_catalogKind}|{Profile.Species.Id}|{Profile.Sex}|{Profile.UnrestrictedMarkings}|{IoCManager.Resolve<ILocalizationManager>().DefaultCulture?.Name}";
        if (key != _selectionCatalogKey)
        {
            _selectionCatalogKey = key;
            _catalogWindow.Panel.SetStaticData(CreateTguiCatalogState().SelectFields(CatalogFields).ToString());
        }
        _catalogWindow.Panel.SetIncrementalState("CharacterCatalog", CreateTguiCatalogState(includeCatalogs: false));
    }
}
