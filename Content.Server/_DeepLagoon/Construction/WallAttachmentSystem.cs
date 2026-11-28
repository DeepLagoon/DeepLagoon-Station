using System.Linq;
using Content.Server.Construction;
using Content.Server.Destructible;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Construction;
using Content.Shared._Mono.NoDeconstruct;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Content.Shared.Tools.Systems;
using Content.Shared.Wall;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Construction;

/// <summary>
/// Tracks wall equipment through construction, map loading and wall destruction.
/// Updates only attachments whose local transform or anchoring changed.
/// </summary>
public sealed class WallAttachmentSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private DestructibleSystem _destructible = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly HashSet<EntityUid> _pending = new();
    private readonly HashSet<EntityUid> _forwarding = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<WallAttachmentComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<WallAttachmentComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<WallAttachmentComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<WallAttachmentComponent, MoveEvent>(OnMoved);
        SubscribeLocalEvent<WallAttachmentComponent, InteractUsingEvent>(OnInteractUsing, before: new[] { typeof(ConstructionSystem) });
        SubscribeLocalEvent<WallAttachmentComponent, WallAttachmentToolDoAfterEvent>(OnToolComplete);
        SubscribeLocalEvent<WallAttachmentComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<WallAttachmentComponent, BeforeDamageChangedEvent>(OnAttachmentDamaged);
        SubscribeLocalEvent<WallAttachmentSupportComponent, BeforeDamageChangedEvent>(OnWallDamaged);
        SubscribeLocalEvent<WallAttachmentSupportComponent, EntityTerminatingEvent>(OnSupportDestroyed);
        SubscribeLocalEvent<WallAttachmentSupportComponent, AnchorStateChangedEvent>(OnSupportAnchorChanged);
        SubscribeLocalEvent<WallAttachmentSupportComponent, MoveEvent>(OnSupportMoved);
    }

    private void OnStartup(EntityUid uid, WallAttachmentComponent attachment, ComponentStartup args)
    {
        // Use the normal wall-mount reach exemption: the support in the same tile
        // must not obstruct tool interactions with its attached equipment.
        var mount = EnsureComp<WallMountComponent>(uid);
        mount.Arc = Angle.FromDegrees(360);
        Dirty(uid, mount);
        _pending.Add(uid);
    }

    public override void Update(float frameTime)
    {
        foreach (var uid in _pending.ToArray())
        {
            _pending.Remove(uid);
            if (TerminatingOrDeleted(uid) || !TryComp<WallAttachmentComponent>(uid, out var attachment))
                continue;
            RefreshBinding(uid, attachment);
        }
    }

    private void OnMoved(Entity<WallAttachmentComponent> ent, ref MoveEvent args) => _pending.Add(ent);

    private void OnAnchorChanged(EntityUid uid, WallAttachmentComponent attachment, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored)
            _pending.Add(uid);
        else
            Unbind(uid, attachment);
    }

    private void OnShutdown(EntityUid uid, WallAttachmentComponent attachment, ComponentShutdown args)
    {
        _pending.Remove(uid);
        Unbind(uid, attachment);
    }

    private void Unbind(EntityUid uid, WallAttachmentComponent attachment)
    {
        if (TryComp<WallAttachmentSupportComponent>(attachment.Support, out var support))
            support.Attachments.Remove(uid);
        attachment.Support = null;
    }

    private EntityUid? FindSupport(EntityUid uid)
    {
        var xform = Transform(uid);
        if (!TryComp<MapGridComponent>(xform.GridUid, out var grid))
            return null;
        var indices = _maps.TileIndicesFor(xform.GridUid.Value, grid, xform.Coordinates);
        foreach (var candidate in _maps.GetAnchoredEntities(xform.GridUid.Value, grid, indices))
        {
            if (candidate != uid && !TerminatingOrDeleted(candidate) && !EntityManager.IsQueuedForDeletion(candidate)
                && _tags.HasTag(candidate, "Wall"))
                return candidate;
        }
        return null;
    }

    private void RefreshBinding(EntityUid uid, WallAttachmentComponent attachment)
    {
        Unbind(uid, attachment);
        if (!Transform(uid).Anchored)
            return;
        if (FindSupport(uid) is not { } wall)
        {
            _transforms.Unanchor(uid);
            return;
        }
        attachment.Support = wall;
        EnsureComp<WallAttachmentSupportComponent>(wall).Attachments.Add(uid);
    }

    private void OnSupportDestroyed(EntityUid uid, WallAttachmentSupportComponent support, ref EntityTerminatingEvent args)
    {
        foreach (var attached in support.Attachments.ToArray())
        {
            if (!TerminatingOrDeleted(attached) && TryComp<WallAttachmentComponent>(attached, out var attachment))
            {
                Unbind(attached, attachment);
                _transforms.Unanchor(attached);
            }
        }
        support.Attachments.Clear();
    }

    private void OnSupportAnchorChanged(EntityUid uid, WallAttachmentSupportComponent support, ref AnchorStateChangedEvent args)
    {
        foreach (var attached in support.Attachments)
            _pending.Add(attached);
    }

    private void OnSupportMoved(Entity<WallAttachmentSupportComponent> ent, ref MoveEvent args)
    {
        foreach (var attached in ent.Comp.Attachments)
            _pending.Add(attached);
    }

    private void OnExamined(EntityUid uid, WallAttachmentComponent attachment, ExaminedEvent args)
        => args.PushMarkup(Loc.GetString(Transform(uid).Anchored ? "examinable-anchored" : "examinable-unanchored", ("target", uid)));

    private void OnInteractUsing(EntityUid uid, WallAttachmentComponent attachment, InteractUsingEvent args)
    {
        if (args.Handled || !_tools.HasQuality(args.Used, "Anchoring") || HasComp<NoDeconstructComponent>(uid)
            || HasComp<ApartmentProtectedGridComponent>(Transform(uid).GridUid))
            return;
        var anchor = !Transform(uid).Anchored;
        if (anchor && FindSupport(uid) == null)
        {
            _popup.PopupEntity(Loc.GetString("wall-attachment-requires-wall"), uid, args.User);
            args.Handled = true;
            return;
        }
        args.Handled = _tools.UseTool(args.Used, args.User, uid, attachment.ToolDelay,
            new[] { "Anchoring" }, new WallAttachmentToolDoAfterEvent(anchor));
    }

    private void OnToolComplete(EntityUid uid, WallAttachmentComponent attachment, WallAttachmentToolDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || HasComp<NoDeconstructComponent>(uid)
            || HasComp<ApartmentProtectedGridComponent>(Transform(uid).GridUid))
            return;
        args.Handled = true;
        if (!args.Anchor)
        {
            _transforms.Unanchor(uid);
            return;
        }
        if (FindSupport(uid) == null)
            return;
        _transforms.AnchorEntity(uid);
        RefreshBinding(uid, attachment);
    }

    private void OnAttachmentDamaged(EntityUid uid, WallAttachmentComponent attachment, ref BeforeDamageChangedEvent args)
    {
        // An explosion hits every entity in the tile. Let the supporting wall route it once,
        // otherwise the same blast damages the equipment independently and again via the wall.
        if (args.OriginFlag == DamageableSystem.DamageOriginFlag.Explosion && attachment.ProtectWall && attachment.Support != null
            && !_forwarding.Contains(uid))
            args.Cancelled = true;
    }

    private void OnWallDamaged(EntityUid uid, WallAttachmentSupportComponent support, ref BeforeDamageChangedEvent args)
    {
        if (args.Cancelled || !args.Damage.AnyPositive())
            return;
        var remaining = new DamageSpecifier(args.ReplacementDamage ?? args.Damage);
        var explosion = args.OriginFlag == DamageableSystem.DamageOriginFlag.Explosion;
        foreach (var attached in support.Attachments.ToArray())
        {
            if (!remaining.AnyPositive())
                break;
            if (TerminatingOrDeleted(attached) || EntityManager.IsQueuedForDeletion(attached)
                || !TryComp<WallAttachmentComponent>(attached, out var attachment) || !attachment.ProtectWall
                || !Transform(attached).Anchored || attachment.Support != uid
                || !TryComp<DamageableComponent>(attached, out var damageable))
                continue;

            var positive = new DamageSpecifier();
            foreach (var (type, amount) in remaining.DamageDict)
                if (amount > 0)
                    positive.DamageDict[type] = amount;
            var effective = positive;
            if (!explosion)
            {
                if (_prototypes.Resolve(damageable.DamageModifierSetId, out var modifiers))
                    effective = DamageSpecifier.ApplyModifierSet(positive, modifiers);
                effective = _damage.ApplyUniversalAllModifiers(new DamageSpecifier(effective));
            }
            var effectiveTotal = effective.GetTotal();
            var available = FixedPoint2.Max(FixedPoint2.Zero, _destructible.DestroyedAt(attached) - damageable.TotalDamage);
            if (available <= 0)
                continue;
            var fraction = 0f;
            _forwarding.Add(attached);
            try
            {
                // Apply the complete hit before measuring absorption. Scaling the raw
                // damage first would incorrectly apply flat reductions to a partial hit.
                var applied = _damage.TryChangeDamage(attached, positive, ignoreResistances: explosion,
                    origin: args.Origin, ignoreGlobalModifiers: explosion, originFlag: args.OriginFlag, tool: args.Tool);
                // Other interceptors, immunity or unsupported damage types may reject the hit.
                // The component can be destroyed here; use the pre-hit durability snapshot
                // and returned delta, without accessing its now potentially deleted component.
                var appliedTotal = applied?.GetTotal().Float() ?? 0f;
                if (applied != null)
                    fraction = effectiveTotal <= 0 ? 1f : appliedTotal > 0
                        ? Math.Clamp(available.Float() / appliedTotal, 0f, 1f)
                        : 0f;
            }
            finally
            {
                _forwarding.Remove(attached);
            }
            foreach (var (type, amount) in positive.DamageDict)
                remaining.DamageDict[type] -= amount * fraction;
        }
        args.ReplacementDamage = remaining;
    }
}
