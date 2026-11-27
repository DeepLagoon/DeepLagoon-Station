using Content.Server.CartridgeLoader;
using Content.Server.PsionicsRecords.Systems;
using Content.Server.Station.Systems;
using Content.Server.StationRecords;
using Content.Server.StationRecords.Systems;
using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Access;
using Content.Shared.Access.Systems;
using Content.Shared.CartridgeLoader;
using Content.Shared.Popups;
using Content.Shared.Psionics;
using Content.Shared.PsionicsRecords;
using Content.Shared.StationRecords;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Mantis;

public sealed partial class PsiRegistryCartridgeSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private CartridgeLoaderSystem _cartridge = default!;
    [Dependency] private PsionicsRecordsConsoleSystem _psionicsConsole = default!;
    [Dependency] private PsionicsRecordsSystem _psionicsRecords = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private StationRecordsSystem _stationRecords = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private static readonly ProtoId<AccessLevelPrototype>[] RegistryAccess =
    [
        "Mantis",
        "HeadOfPersonnel",
    ];

    private const int MaxReasonLength = 256;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PsiRegistryCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<PsiRegistryCartridgeComponent, CartridgeMessageEvent>(OnMessage);

        SubscribeLocalEvent<RecordModifiedEvent>(OnRecordModified);
    }

    private void OnUiReady(Entity<PsiRegistryCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateUi(ent, args.Loader);
    }

    private void OnRecordModified(RecordModifiedEvent args)
    {
        var query = EntityQueryEnumerator<PsiRegistryCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out var uid, out var component, out var cartridge))
        {
            if (cartridge.LoaderUid is not { } loader)
                continue;

            if (!TryComp<CartridgeLoaderComponent>(loader, out var loaderComp)
                || !_ui.IsUiOpen(loader, loaderComp.UiKey))
                continue;

            UpdateUi((uid, component), loader);
        }
    }

    private void OnMessage(Entity<PsiRegistryCartridgeComponent> ent, ref CartridgeMessageEvent args)
    {
        var loaderUid = GetEntity(args.LoaderUid);

        switch (args)
        {
            case PsiRegistrySelectMessage select:
                Select(ent, loaderUid, select.Key);
                break;

            case PsiRegistryChangeStatusMessage change:
                TryChangeStatus(ent, loaderUid, args.Actor, change.Status, change.Reason);
                break;

            default:
                return;
        }

        UpdateUi(ent, loaderUid);
    }

    private void Select(Entity<PsiRegistryCartridgeComponent> ent, EntityUid loaderUid, uint? id)
    {
        if (id is not { } key)
        {
            ent.Comp.SelectedKey = null;
            return;
        }

        ent.Comp.SelectedKey = new StationRecordKey(key, StationOf(loaderUid));
    }

    private void TryChangeStatus(Entity<PsiRegistryCartridgeComponent> ent, EntityUid loaderUid, EntityUid user,
        PsionicsStatus status, string? reason)
    {
        if (!CanEdit(user))
        {
            _popup.PopupEntity(Loc.GetString("psi-registry-permission-denied"), loaderUid, user);
            return;
        }

        if (!TryGetSelected(ent, loaderUid, out var key))
            return;

        if (!_stationRecords.TryGetRecord<PsionicsRecord>(key, out var record) || record.Status == status)
            return;

        string? newReason = null;
        if (status != PsionicsStatus.None)
        {
            newReason = reason?.Trim();
            if (string.IsNullOrEmpty(newReason) || newReason.Length > MaxReasonLength)
                return;
        }

        if (!_psionicsRecords.TryChangeStatus(key, status, newReason))
            return;

        _psionicsConsole.UpdatePsionicsIdentity(_stationRecords.RecordName(key), status);
    }

    private bool TryGetSelected(Entity<PsiRegistryCartridgeComponent> ent, EntityUid loaderUid,
        out StationRecordKey key)
    {
        key = default;

        if (ent.Comp.SelectedKey is not { } selected)
            return false;

        if (selected.OriginStation != StationOf(loaderUid))
            return false;

        key = selected;
        return true;
    }

    private EntityUid StationOf(EntityUid loaderUid)
    {
        return _station.GetOwningStation(loaderUid) ?? EntityUid.Invalid;
    }

    private bool CanEdit(EntityUid user)
    {
        if (!user.IsValid())
            return false;

        var tags = _access.FindAccessTags(user);

        foreach (var required in RegistryAccess)
        {
            if (tags.Contains(required))
                return true;
        }

        return false;
    }

    private void UpdateUi(Entity<PsiRegistryCartridgeComponent> ent, EntityUid loaderUid)
    {
        var station = StationOf(loaderUid);
        var records = new List<PsiRegistryEntry>();
        uint? selected = null;

        if (TryComp<StationRecordsComponent>(station, out var stationRecords))
        {
            foreach (var (id, record) in _stationRecords.GetRecordsOfType<PsionicsRecord>(station, stationRecords))
            {
                _stationRecords.TryGetRecord<GeneralStationRecord>(
                    new StationRecordKey(id, station), out var general);

                records.Add(new PsiRegistryEntry(
                    id,
                    general?.Name ?? string.Empty,
                    general?.JobTitle ?? string.Empty,
                    record.Status,
                    record.Reason));

                if (ent.Comp.SelectedKey is { } key && key.Id == id)
                    selected = id;
            }
        }

        if (selected == null)
            ent.Comp.SelectedKey = null;

        var state = new PsiRegistryUiState(records, selected, CanEdit(ViewerOf(loaderUid)));
        _cartridge.UpdateCartridgeUiState(loaderUid, state);
    }

    private EntityUid ViewerOf(EntityUid loaderUid)
    {
        if (!TryComp<CartridgeLoaderComponent>(loaderUid, out var loader))
            return EntityUid.Invalid;

        foreach (var actor in _ui.GetActors(loaderUid, loader.UiKey))
        {
            return actor;
        }

        return EntityUid.Invalid;
    }
}
