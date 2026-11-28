using System.Numerics;
using Content.Server.PowerCell;
using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Anomaly.Components;
using Content.Shared.Hands;
using Content.Shared.NPC.Systems;
using Content.Shared.Power.Components;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Content.Shared.Psionics;
using Content.Shared.Psionics.Glimmer;
using Content.Shared.Revenant.Components;
using Content.Shared.Tag;
using Content.Shared.UserInterface;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Mantis;

public sealed partial class PsiSpectrometerSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly GlimmerSystem _glimmer = default!;
    [Dependency] private readonly NpcFactionSystem _factions = default!;
    [Dependency] private readonly SharedPopupSystem _popups = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly PowerCellSystem _cell = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly HashSet<string> KineticPowers =
    [
        "TelekineticPulsePower",
        "PyrokinesisPower",
        "PyrokineticFlare",
    ];

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<PsiSpectrometerComponent>(PsiSpectrometerUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<PsiSpectrometerScanMessage>(OnScan);
            subs.Event<PsiSpectrometerHunterMessage>(OnHunter);
            subs.Event<PsiSpectrometerMapMessage>(OnMapOverlay);
        });

        SubscribeLocalEvent<PsiSpectrometerComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<PsiSpectrometerComponent, GotEquippedHandEvent>(OnPickedUp);
        SubscribeLocalEvent<PsiSpectrometerComponent, GotUnequippedHandEvent>(OnDropped);
    }

    private void OnPickedUp(Entity<PsiSpectrometerComponent> ent, ref GotEquippedHandEvent args)
    {
        if (ent.Comp.HunterMode)
            ApplyHunterFaction(ent);
    }

    private void OnDropped(Entity<PsiSpectrometerComponent> ent, ref GotUnequippedHandEvent args)
    {
        if (ent.Comp.HunterMode)
            ClearHunterFaction(ent);
    }

    private void OnOpened(Entity<PsiSpectrometerComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnHunter(Entity<PsiSpectrometerComponent> ent, ref PsiSpectrometerHunterMessage args)
    {
        SetHunterMode(ent, args.Enabled);
        UpdateUi(ent);
    }

    private void OnMapOverlay(Entity<PsiSpectrometerComponent> ent, ref PsiSpectrometerMapMessage args)
    {
        if (ent.Comp.MapOverlay == args.Enabled)
            return;

        ent.Comp.MapOverlay = args.Enabled;
        Dirty(ent);
        UpdateUi(ent);
    }

    private void OnScan(Entity<PsiSpectrometerComponent> ent, ref PsiSpectrometerScanMessage args)
    {
        var user = args.Actor;

        if (!CanScan(ent, user))
            return;

        ent.Comp.NextScan = _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.ScanDelay);
        ent.Comp.LastSignatures = Sweep(ent);
        ent.Comp.LastGlimmer = _glimmer.GetGlimmerOutputInteger();

        Dirty(ent);

        if (ent.Comp.LastSignatures.Count == 0)
            _popups.PopupEntity(Loc.GetString("psi-spectrometer-empty"), ent, user, PopupType.Medium);
        else
            _popups.PopupEntity(Loc.GetString("psi-spectrometer-found",
                ("count", ent.Comp.LastSignatures.Count)), ent, user, PopupType.Medium);

        UpdateUi(ent);
    }

    private bool CanScan(Entity<PsiSpectrometerComponent> ent, EntityUid user)
    {
        if (ent.Comp.NextScan > _timing.CurTime)
        {
            _popups.PopupEntity(Loc.GetString("psi-spectrometer-recharging"), ent, user, PopupType.SmallCaution);
            return false;
        }

        if (ent.Comp.RequiresPower &&
            (!TryComp<SharedApcPowerReceiverComponent>(ent, out var power) || !power.Powered))
        {
            _popups.PopupEntity(Loc.GetString("psi-spectrometer-unpowered"), ent, user, PopupType.SmallCaution);
            return false;
        }

        if (ent.Comp.ChargePerScan <= 0f)
            return true;

        if (_cell.TryUseCharge(ent.Owner, ent.Comp.ChargePerScan))
            return true;

        _popups.PopupEntity(Loc.GetString("psi-spectrometer-no-charge"), ent, user, PopupType.SmallCaution);
        return false;
    }

    private List<NoosphericSignature> Sweep(Entity<PsiSpectrometerComponent> ent)
    {
        var found = new List<NoosphericSignature>();
        var range = EffectiveRange(ent.Comp);

        foreach (var other in _lookup.GetEntitiesInRange(ent, range))
        {
            if (TryReadSignature(ent, other, out var signature))
                found.Add(signature);
        }

        found.Sort((a, b) => b.Intensity.CompareTo(a.Intensity));
        return found;
    }

    public bool TryReadSignature(Entity<PsiSpectrometerComponent> ent, EntityUid subject,
        out NoosphericSignature signature)
    {
        signature = default!;

        if (subject == ent.Owner || HasComp<PsionicInsulationComponent>(subject))
            return false;

        if (!TryClassify(subject, out var type))
            return false;

        var range = EffectiveRange(ent.Comp);
        var subjectPosition = Transform(subject).Coordinates.Position;
        var delta = subjectPosition - Transform(ent).Coordinates.Position;
        var intensity = range <= 0f ? 1f : Math.Clamp(1f - delta.Length() / range, 0.05f, 1f);

        signature = new NoosphericSignature(type, SectorOf(delta), intensity, Name(subject), subjectPosition);
        return true;
    }

    private bool TryClassify(EntityUid uid, out NoosphericSignatureType type)
    {
        if (_tags.HasTag(uid, "NoosphericSignatureIgnored"))
        {
            type = default;
            return false;
        }

        if (_tags.HasTag(uid, "NoosphericSignatureAnomalous"))
        {
            type = NoosphericSignatureType.Anomalous;
            return true;
        }

        if (_tags.HasTag(uid, "NoosphericSignatureKinetic"))
        {
            type = NoosphericSignatureType.Kinetic;
            return true;
        }

        if (_tags.HasTag(uid, "NoosphericSignatureSapient"))
        {
            type = NoosphericSignatureType.Sapient;
            return true;
        }

        if (_factions.IsMember(uid, "GlimmerMonster")
            || HasComp<AnomalyComponent>(uid)
            || HasComp<RevenantComponent>(uid))
        {
            type = NoosphericSignatureType.Anomalous;
            return true;
        }

        if (TryComp<PsionicComponent>(uid, out var psionic))
        {
            type = ClassifyPsionic(psionic);
            return true;
        }

        type = default;
        return false;
    }

    private static NoosphericSignatureType ClassifyPsionic(PsionicComponent psionic)
    {
        var total = 0;
        var kinetic = 0;

        foreach (var power in psionic.ActivePowers)
        {
            total++;
            if (KineticPowers.Contains(power.ID))
                kinetic++;
        }

        return total > 0 && kinetic * 2 > total
            ? NoosphericSignatureType.Kinetic
            : NoosphericSignatureType.Sapient;
    }

    private NoosphericSector SectorOf(Vector2 delta)
    {
        if (delta.LengthSquared() < 0.01f)
            return NoosphericSector.Self;

        var index = (int) Math.Floor((Math.Atan2(delta.Y, delta.X) + Math.PI) / (Math.PI / 4)) % 8;

        return index switch
        {
            0 => NoosphericSector.West,
            1 => NoosphericSector.NorthWest,
            2 => NoosphericSector.North,
            3 => NoosphericSector.NorthEast,
            4 => NoosphericSector.East,
            5 => NoosphericSector.SouthEast,
            6 => NoosphericSector.South,
            _ => NoosphericSector.SouthWest,
        };
    }

    public float EffectiveRange(PsiSpectrometerComponent component) =>
        component.HunterMode ? component.Range * component.HunterRangeMultiplier : component.Range;

    public void SetHunterMode(Entity<PsiSpectrometerComponent> ent, bool enabled)
    {
        if (ent.Comp.HunterMode == enabled)
            return;

        ent.Comp.HunterMode = enabled;

        if (enabled)
            ApplyHunterFaction(ent);
        else
            ClearHunterFaction(ent);

        Dirty(ent);

        _popups.PopupEntity(Loc.GetString(enabled ? "psi-spectrometer-hunter-on" : "psi-spectrometer-hunter-off"),
            ent, PopupType.Medium);
    }

    private void ApplyHunterFaction(Entity<PsiSpectrometerComponent> ent)
    {
        var holder = Transform(ent).ParentUid;
        if (!holder.IsValid())
            return;

        ClearHunterFaction(ent);

        ent.Comp.HunterHolder = holder;
        _factions.AddFaction(holder, ent.Comp.HunterFaction);
    }

    private void ClearHunterFaction(Entity<PsiSpectrometerComponent> ent)
    {
        if (ent.Comp.HunterHolder is not { } previous || !previous.IsValid())
            return;

        ent.Comp.HunterHolder = null;

        if (!TerminatingOrDeleted(previous))
            _factions.RemoveFaction(previous, ent.Comp.HunterFaction);
    }

    private void OnShutdown(Entity<PsiSpectrometerComponent> ent, ref ComponentShutdown args)
    {
        if (!ent.Comp.HunterMode)
            return;

        ent.Comp.HunterMode = false;
        ClearHunterFaction(ent);
    }

    private void UpdateUi(Entity<PsiSpectrometerComponent> ent)
    {
        if (!_ui.IsUiOpen(ent.Owner, PsiSpectrometerUiKey.Key))
            return;

        var ready = CanAffordScan(ent);

        _ui.SetUiState(ent.Owner, PsiSpectrometerUiKey.Key, new PsiSpectrometerState(
            ent.Comp.LastSignatures,
            ent.Comp.LastGlimmer,
            ent.Comp.HunterMode,
            ready,
            EffectiveRange(ent.Comp),
            ent.Comp.MapOverlay));
    }

    private bool CanAffordScan(Entity<PsiSpectrometerComponent> ent)
    {
        if (ent.Comp.RequiresPower &&
            (!TryComp<SharedApcPowerReceiverComponent>(ent, out var power) || !power.Powered))
        {
            return false;
        }

        if (ent.Comp.ChargePerScan <= 0f)
            return true;

        var cell = CompOrNull<PowerCellSlotComponent>(ent.Owner);
        return _cell.HasCharge(ent.Owner, ent.Comp.ChargePerScan, cell);
    }
}
