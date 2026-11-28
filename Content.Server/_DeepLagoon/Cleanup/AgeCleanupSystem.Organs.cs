using System.Numerics;
using Content.Server._Mono.Cleanup;
using Content.Shared.Body.Organ;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Cleanup;

public sealed partial class AgeCleanupSystem
{
    [Dependency] private IPlayerManager _organPlayers = default!;
    [Dependency] private SharedTransformSystem _organTransforms = default!;

    private GameTick? _organObserverTick;
    private readonly List<(EntityUid Map, Vector2 Position, float Range)> _organObservers = new();

    private void InitializeOrgans()
    {
        SubscribeLocalEvent<OrganComponent, EntParentChangedMessage>(OnOrganParentChanged);
    }

    private void OnOrganParentChanged(Entity<OrganComponent> ent, ref EntParentChangedMessage args)
    {
        // A new drop starts a fresh continuous ten minutes, not the age since the organ spawned.
        if (IsLooseOrgan(ent) && !HasComp<AgeCleanupComponent>(ent))
            Mark(ent, AgeCleanupKind.LooseOrgan, TimeSpan.FromMinutes(10));
    }

    private bool IsLooseOrgan(EntityUid uid)
    {
        if (!HasComp<OrganComponent>(uid) || HasComp<CleanupImmuneComponent>(uid) || HasComp<ActorComponent>(uid) ||
            !TryComp<TransformComponent>(uid, out var xform) || xform.Anchored || xform.MapUid == null ||
            _containers.IsEntityInContainer(uid))
            return false;
        return xform.ParentUid == xform.MapUid || HasComp<MapGridComponent>(xform.ParentUid);
    }

    private bool IsOrganObserved(EntityUid uid)
    {
        // Include subscribed camera/remote eyes and ghosts, not only a nearby living body.
        // Cache for this simulation tick so a pile of old organs does not rescan players per item.
        if (_organObserverTick != _clock.CurTick)
        {
            _organObserverTick = _clock.CurTick;
            _organObservers.Clear();
            foreach (var session in _organPlayers.Sessions)
            {
                if (session.Status != SessionStatus.InGame) continue;
                if (session.AttachedEntity is { } attached) AddOrganObserver(attached);
                foreach (var eye in session.ViewSubscriptions) AddOrganObserver(eye);
            }
        }
        var xform = Transform(uid);
        var position = _organTransforms.GetWorldPosition(xform);
        foreach (var view in _organObservers)
            if (view.Map == xform.MapUid && MathF.Abs(position.X - view.Position.X) <= view.Range &&
                MathF.Abs(position.Y - view.Position.Y) <= view.Range)
                return true;
        return false;
    }

    private void AddOrganObserver(EntityUid uid)
    {
        if (!TryComp<TransformComponent>(uid, out var xform) || xform.MapUid is not { } map) return;
        var position = _organTransforms.GetWorldPosition(xform);
        var range = MathF.Max(16f, MathF.Max(_configuration.GetCVar(CVars.NetMaxUpdateRange),
            _configuration.GetCVar(CVars.NetPvsPriorityRange)));
        if (TryComp<EyeComponent>(uid, out var eye))
        {
            position += eye.Offset;
            range *= MathF.Max(1f, eye.PvsScale);
        }
        // Conservatively protect the whole potential view area, even behind walls or in darkness.
        _organObservers.Add((map, position, range + 8f));
    }
}
