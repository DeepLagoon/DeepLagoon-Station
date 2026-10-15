using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.GridMagnet;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Server.Physics;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.GridMagnet;

/// <summary>
/// Runs <see cref="GridMagnetComponent"/>: pairs up active machines on different grids, pulls the grid that is
/// not kept to the other one, and merges it into the kept grid. The pull moves the grid through its transform,
/// so it works the same for static station grids and dynamic shuttles. The actual merge is the engine's
/// <see cref="GridFixtureSystem.Merge"/> plus the fix-ups it does not do (see the Merge partial).
/// </summary>
public sealed partial class GridMagnetSystem : EntitySystem
{
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ISharedAdminLogManager _adminLog = default!;
    [Dependency] private GridFixtureSystem _gridFixture = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private DecalSystem _decals = default!;

    /// <summary>
    /// How often idle active machines look for a partner.
    /// </summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far the pulled grid may drift from where this system last put it before the pull is given up.
    /// Something else (an explosion, a thruster) moved it.
    /// </summary>
    private const float MaxDisturbance = 1f;

    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<GridMagnetComponent> _magnetQuery;

    private TimeSpan _nextScan;

    private readonly List<Pair> _pairs = new();
    private readonly HashSet<EntityUid> _busy = new();
    private readonly List<Entity<GridMagnetComponent, TransformComponent>> _scanBuffer = new();

    public override void Initialize()
    {
        base.Initialize();

        _xformQuery = GetEntityQuery<TransformComponent>();
        _magnetQuery = GetEntityQuery<GridMagnetComponent>();

        SubscribeLocalEvent<GridMagnetComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<GridMagnetComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<GridMagnetComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<GridMagnetComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<GridMagnetComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _pairs.Clear();
        _busy.Clear();
        _pendingGas.Clear();
    }

    private void OnStartup(Entity<GridMagnetComponent> ent, ref ComponentStartup args)
    {
        UpdateAppearance(ent);
    }

    private void OnActivate(Entity<GridMagnetComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        Toggle(ent, args.User);
    }

    private void OnGetVerbs(Entity<GridMagnetComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString(ent.Comp.Active ? "dl-grid-magnet-verb-off" : "dl-grid-magnet-verb-on"),
            Priority = 1,
            Act = () => Toggle(ent, user),
        });
    }

    private void OnAnchorChanged(Entity<GridMagnetComponent> ent, ref AnchorStateChangedEvent args)
    {
        // A machine that comes loose switches itself off; the pair notices on its next tick.
        if (!args.Anchored && ent.Comp.Active)
            SetActive(ent, false);
    }

    private void OnExamined(Entity<GridMagnetComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString(ent.Comp.Active
            ? "dl-grid-magnet-examine-active"
            : "dl-grid-magnet-examine-inactive"));
    }

    private void Toggle(Entity<GridMagnetComponent> ent, EntityUid user)
    {
        if (!ent.Comp.Active && !Transform(ent).Anchored)
        {
            _popup.PopupEntity(Loc.GetString("dl-grid-magnet-needs-anchor"), ent, user);
            return;
        }

        if (!ent.Comp.Active && !HasPower(ent, ent.Comp))
        {
            _popup.PopupEntity(Loc.GetString("dl-grid-magnet-needs-power"), ent, user);
            return;
        }

        SetActive(ent, !ent.Comp.Active);
        _popup.PopupEntity(Loc.GetString(ent.Comp.Active ? "dl-grid-magnet-switched-on" : "dl-grid-magnet-switched-off"),
            ent, user);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(user):user} switched {(ent.Comp.Active ? "on" : "off")} the grid magnet {ToPrettyString(ent):entity}");
    }

    private void SetActive(Entity<GridMagnetComponent> ent, bool active)
    {
        if (ent.Comp.Active == active)
            return;

        ent.Comp.Active = active;
        Dirty(ent);
        UpdateAppearance(ent);
    }

    private void UpdateAppearance(Entity<GridMagnetComponent> ent)
    {
        _appearance.SetData(ent, GridMagnetVisuals.Active, ent.Comp.Active);
    }

    private void Notify(EntityUid machine, string locKey, params (string, object)[] args)
    {
        if (TerminatingOrDeleted(machine))
            return;

        _popup.PopupEntity(Loc.GetString(locKey, args), machine, PopupType.Medium);
    }

    private void NotifyBoth(Pair pair, string locKey, params (string, object)[] args)
    {
        Notify(pair.Keeper, locKey, args);
        Notify(pair.Pulled, locKey, args);
    }

    /// <summary>
    /// A machine takes part in pairing only when it is switched on, bolted down on a grid and, if the power
    /// hookup is enabled, powered.
    /// </summary>
    private bool IsOperational(EntityUid uid, GridMagnetComponent magnet, TransformComponent xform)
    {
        if (!magnet.Active || !xform.Anchored || TerminatingOrDeleted(uid))
            return false;

        if (xform.GridUid is not { } grid || xform.MapUid == null || !HasComp<MapGridComponent>(grid))
            return false;

        return HasPower(uid, magnet);
    }

    private bool HasPower(EntityUid uid, GridMagnetComponent magnet)
    {
        if (!magnet.RequirePower)
            return true;

        return TryComp(uid, out ApcPowerReceiverComponent? receiver) && receiver.Powered;
    }

    public override void Update(float frameTime)
    {
        var now = _timing.CurTime;

        UpdatePendingGas(now);

        for (var i = _pairs.Count - 1; i >= 0; i--)
        {
            UpdatePair(_pairs[i], now);
        }

        if (now >= _nextScan)
        {
            _nextScan = now + ScanInterval;
            Scan(now);
        }
    }

    /// <summary>
    /// Looks for pairs among the active machines that do not belong to a pair yet.
    /// </summary>
    private void Scan(TimeSpan now)
    {
        _scanBuffer.Clear();

        var query = EntityQueryEnumerator<GridMagnetComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var magnet, out var xform))
        {
            if (!_busy.Contains(uid) && IsOperational(uid, magnet, xform))
                _scanBuffer.Add((uid, magnet, xform));
        }

        for (var i = 0; i < _scanBuffer.Count; i++)
        {
            var a = _scanBuffer[i];
            if (_busy.Contains(a.Owner))
                continue;

            var bestDistance = float.MaxValue;
            var bestIndex = -1;

            for (var j = i + 1; j < _scanBuffer.Count; j++)
            {
                var b = _scanBuffer[j];
                if (_busy.Contains(b.Owner) || !CanPair(a, b))
                    continue;

                var distance = (_transform.GetWorldPosition(a.Comp2) - _transform.GetWorldPosition(b.Comp2)).Length();
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestIndex = j;
            }

            if (bestIndex >= 0)
                StartPair(a, _scanBuffer[bestIndex], now);
        }

        _scanBuffer.Clear();
    }

    /// <summary>
    /// Whether two machines can pull on each other right now: different grids on one map, within range of both,
    /// and facing each other within the tolerance of both.
    /// </summary>
    private bool CanPair(Entity<GridMagnetComponent, TransformComponent> a, Entity<GridMagnetComponent, TransformComponent> b)
    {
        var xa = a.Comp2;
        var xb = b.Comp2;

        if (!IsOperational(a, a.Comp1, xa) || !IsOperational(b, b.Comp1, xb))
            return false;

        if (xa.GridUid == xb.GridUid || xa.MapID != xb.MapID)
            return false;

        var delta = _transform.GetWorldPosition(xb) - _transform.GetWorldPosition(xa);
        var distance = delta.Length();
        if (distance > MathF.Min(a.Comp1.Range, b.Comp1.Range))
            return false;

        var faceA = _transform.GetWorldRotation(xa).ToWorldVec();
        var faceB = _transform.GetWorldRotation(xb).ToWorldVec();

        // Zero tolerance would fail on float noise alone.
        var tolerance = MathF.Max(MathF.Min(a.Comp1.AngleTolerance, b.Comp1.AngleTolerance), 0.5f);
        if (Vector2.Dot(faceA, -faceB) < MathF.Cos(MathHelper.DegreesToRadians(tolerance)))
            return false;

        // Each machine has to be on the side the other one faces, so two machines back to back never pair.
        if (distance > 0.01f && (Vector2.Dot(faceA, delta) <= 0f || Vector2.Dot(faceB, -delta) <= 0f))
            return false;

        return true;
    }

    private void StartPair(Entity<GridMagnetComponent, TransformComponent> a, Entity<GridMagnetComponent, TransformComponent> b, TimeSpan now)
    {
        if (!TryBuildPlan(a, b, out var plan, out var error))
        {
            // A pair that can never merge would be found again every second, so both machines go idle.
            Notify(a, "dl-grid-magnet-failed", ("reason", error!));
            Notify(b, "dl-grid-magnet-failed", ("reason", error!));
            SetActive((a, a.Comp1), false);
            SetActive((b, b.Comp1), false);
            return;
        }

        var keeperMagnet = Comp<GridMagnetComponent>(plan!.Keeper);
        var pair = new Pair(plan)
        {
            Phase = PairPhase.Charging,
            PhaseEnd = now + TimeSpan.FromSeconds(keeperMagnet.StartDelay),
        };

        _pairs.Add(pair);
        _busy.Add(pair.Keeper);
        _busy.Add(pair.Pulled);

        NotifyBoth(pair, "dl-grid-magnet-charging", ("seconds", MathF.Round(keeperMagnet.StartDelay)));
    }

    private void UpdatePair(Pair pair, TimeSpan now)
    {
        if (!StillValid(pair, out var silent))
        {
            EndPair(pair, deactivate: false, silent ? null : "dl-grid-magnet-aborted");
            return;
        }

        var keeperMagnet = Comp<GridMagnetComponent>(pair.Keeper);

        switch (pair.Phase)
        {
            case PairPhase.Charging:
            {
                // Nothing moves yet, so the machines still have to line up as they did when they paired.
                if (!CanPair((pair.Keeper, keeperMagnet, Transform(pair.Keeper)),
                        (pair.Pulled, Comp<GridMagnetComponent>(pair.Pulled), Transform(pair.Pulled))))
                {
                    EndPair(pair, deactivate: false, "dl-grid-magnet-aborted");
                    return;
                }

                if (now >= pair.PhaseEnd)
                    BeginPull(pair, keeperMagnet, now);

                break;
            }
            case PairPhase.Pulling:
            {
                if (!MovePulled(pair, now))
                {
                    // Idle both: whatever moved the grid would otherwise make them pair and lose it again.
                    EndPair(pair, deactivate: true, "dl-grid-magnet-lost-grip");
                    return;
                }

                if (now >= pair.PullEnd)
                {
                    pair.Phase = PairPhase.Welding;
                    pair.PhaseEnd = now + TimeSpan.FromSeconds(keeperMagnet.WeldTime);
                    NotifyBoth(pair, "dl-grid-magnet-welding", ("seconds", MathF.Round(keeperMagnet.WeldTime)));
                }

                break;
            }
            case PairPhase.Welding:
            {
                if (!MovePulled(pair, now))
                {
                    EndPair(pair, deactivate: true, "dl-grid-magnet-lost-grip");
                    return;
                }

                if (now >= pair.PhaseEnd)
                    Weld(pair);

                break;
            }
        }
    }

    /// <summary>
    /// A pair survives while both machines exist, are switched on and anchored on the grids they paired on.
    /// </summary>
    /// <param name="silent">True when someone switched a machine off, so no extra message is needed.</param>
    private bool StillValid(Pair pair, out bool silent)
    {
        silent = false;

        if (TerminatingOrDeleted(pair.KeeperGrid) || TerminatingOrDeleted(pair.PulledGrid))
            return false;

        foreach (var uid in new[] { pair.Keeper, pair.Pulled })
        {
            if (!_magnetQuery.TryGetComponent(uid, out var magnet) ||
                !_xformQuery.TryGetComponent(uid, out var xform) ||
                TerminatingOrDeleted(uid))
            {
                return false;
            }

            if (!magnet.Active)
            {
                silent = true;
                return false;
            }

            var expectedGrid = uid == pair.Keeper ? pair.KeeperGrid : pair.PulledGrid;
            if (!IsOperational(uid, magnet, xform) || xform.GridUid != expectedGrid)
                return false;
        }

        return true;
    }

    private void EndPair(Pair pair, bool deactivate, string? message)
    {
        _pairs.Remove(pair);
        _busy.Remove(pair.Keeper);
        _busy.Remove(pair.Pulled);

        if (message != null)
            NotifyBoth(pair, message);

        if (!deactivate)
            return;

        foreach (var uid in new[] { pair.Keeper, pair.Pulled })
        {
            if (_magnetQuery.TryGetComponent(uid, out var magnet) && !TerminatingOrDeleted(uid))
                SetActive((uid, magnet), false);
        }
    }
}
