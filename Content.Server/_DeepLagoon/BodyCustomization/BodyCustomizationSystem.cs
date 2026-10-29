using System.Linq;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Content.Shared.FixedPoint;
using Content.Shared.Mind;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.BodyCustomization;

public sealed partial class BodyCustomizationSystem : EntitySystem
{
    [Dependency] private INetConfigurationManager _cfg = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    private float _preferenceTimer;

    public const int UnitsPerStep = 5;
    private static readonly int DosePerStep = FixedPoint2.New(UnitsPerStep).Value;

    public override void Initialize()
    {
        SubscribeLocalEvent<PlayerAttachedEvent>(ev =>
        {
            if (HasComp<HumanoidAppearanceComponent>(ev.Entity)) SyncPreference(ev.Entity);
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _preferenceTimer += frameTime;
        if (_preferenceTimer < .25f) return;
        _preferenceTimer = 0;
        var query = EntityQueryEnumerator<BodySizeDoseComponent>();
        while (query.MoveNext(out var uid, out var progress)) SyncPreference(uid, progress);
    }

    private bool SyncPreference(EntityUid uid, BodySizeDoseComponent? progress = null)
    {
        progress ??= EnsureComp<BodySizeDoseComponent>(uid);
        ICommonSession? session = null;
        if (_mind.TryGetMind(uid, out _, out var mind) && mind.UserId is { } user
            && _players.TryGetSessionById(user, out var owner)) session = owner;
        else if (_players.TryGetSessionByEntity(uid, out var attached)) session = attached;
        if (session != null && session.Status != SessionStatus.Disconnected)
            progress.ChemicalChangesAllowed = _cfg.GetClientCVar(session.Channel, BodyCustomizationCVars.ChemicalChanges);
        if (!progress.ChemicalChangesAllowed) progress.Doses.Clear();
        return progress.ChemicalChangesAllowed;
    }

    public bool ApplyDose(EntityUid uid, string part, int direction, FixedPoint2 units, bool allowCreation = true)
    {
        if (!BodyCustomizationCatalog.CanResize(part) || direction is not (-1 or 1)
            || units <= 0
            || !TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)) return false;
        var features = BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures);
        var progress = EnsureComp<BodySizeDoseComponent>(uid);
        if (!SyncPreference(uid, progress)) return false;
        if (!features.Any(f => f.Part == part && f.Enabled) && (direction < 0 || !allowCreation))
        {
            progress.Doses.Remove(part);
            return false;
        }
        var dose = progress.Doses.GetValueOrDefault(part) + (long) units.Value * direction;
        var changed = false;
        if (!features.Any(f => f.Part == part && f.Enabled))
        {
            var feature = features.FirstOrDefault(f => f.Part == part);
            if (feature == null) { feature = BodyCustomizationCatalog.Default(part); features.Add(feature); }
            feature.Enabled = true;
            feature.Size = BodyCustomizationCatalog.Sizes(part, feature.Shape).First();
            changed = true;
        }
        while (Math.Abs(dose) >= DosePerStep)
        {
            var step = Math.Sign(dose);
            var feature = features.FirstOrDefault(f => f.Part == part);
            if (feature == null || !feature.Enabled)
            {
                if (feature == null)
                {
                    feature = BodyCustomizationCatalog.Default(part);
                    features.Add(feature);
                }
                feature.Enabled = true;
                feature.Size = BodyCustomizationCatalog.Sizes(part, feature.Shape).First();
            }
            else if (!BodyCustomizationCatalog.Resize(features, part, step))
            {
                // Do not bank excess doses at a size boundary or loop over huge quantities.
                dose %= DosePerStep;
                break;
            }
            changed = true;
            dose -= DosePerStep * step;
        }
        progress.Doses[part] = dose;
        if (!changed) return false;
        humanoid.BodyFeatures = features;
        Changed(uid, humanoid);
        return true;
    }

    public bool SetVisibility(EntityUid uid, string part, int visibility)
    {
        if (visibility is < 0 or > 2 || part == "womb"
            || !TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)) return false;
        var features = BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures);
        var feature = features.FirstOrDefault(f => f.Part == part && f.Enabled);
        if (feature == null) return false;
        feature.Visibility = visibility;
        humanoid.BodyFeatures = features;
        Changed(uid, humanoid);
        return true;
    }

    public bool SetGenitalArousal(EntityUid uid, bool? aroused = null)
    {
        if (!TryComp<HumanoidAppearanceComponent>(uid, out var humanoid) ||
            !humanoid.BodyFeatures.Any(f => f.Part == "penis" && f.Enabled)) return false;
        var next = aroused ?? !humanoid.GenitalAroused;
        if (humanoid.GenitalAroused == next) return true;
        humanoid.GenitalAroused = next;
        EnsureComp<GenitalArousalCooldownComponent>(uid).NextAutomaticChange = _timing.CurTime + GenitalArousalCooldown;
        Changed(uid, humanoid);
        return true;
    }

    private void Changed(EntityUid uid, HumanoidAppearanceComponent humanoid)
    {
        Dirty(uid, humanoid);
        var ev = new BodyCustomizationChangedEvent();
        RaiseLocalEvent(uid, ref ev);
    }

    public bool AllowsPhysiologyChanges(EntityUid uid) => SyncPreference(uid);

    public bool SetPhysiologyFeature(EntityUid uid, string part, BodyFeature? feature)
    {
        if (!TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)) return false;
        var features = BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures);
        features.RemoveAll(f => f.Part == part);
        if (feature != null && feature.Part == part) features.Add(feature.Clone());
        humanoid.BodyFeatures = BodyCustomizationCatalog.Sanitize(features);
        Changed(uid, humanoid);
        return true;
    }
}
