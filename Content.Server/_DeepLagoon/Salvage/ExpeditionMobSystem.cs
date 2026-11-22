using System.Linq;
using Content.Server.Body.Systems;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Body.Components;
using Content.Shared.Gibbing.Components;
using Content.Shared.Gibbing.Events;
using Content.Shared.Gibbing.Systems;
using Content.Shared.Mobs.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;

namespace Content.Server._DeepLagoon.Salvage;

/// <summary>Gibs expedition-born mobs when they leave, including inside moving grids and containers.</summary>
public sealed partial class ExpeditionMobSystem : EntitySystem
{
    [Dependency] private BodySystem _body = default!;
    [Dependency] private GibbingSystem _gibbing = default!;
    [Dependency] private MetaDataSystem _meta = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly HashSet<EntityUid> _pending = new();

    public override void Initialize()
    {
        base.Initialize();
        EntityManager.EntityInitialized += OnMobInitialized;
        SubscribeLocalEvent<ExpeditionMobComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<ExpeditionMobComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<ExpeditionMobComponent, MetaFlagRemoveAttemptEvent>(OnMetaFlagRemoveAttempt);
        SubscribeLocalEvent<ExpeditionMobComponent, MapUidChangedEvent>(OnMapChanged);
    }

    public override void Shutdown()
    {
        EntityManager.EntityInitialized -= OnMobInitialized;
        base.Shutdown();
    }

    private void OnMobInitialized(Entity<MetaDataComponent> ent)
    {
        if (!HasComp<MobStateComponent>(ent) || HasComp<ExpeditionMobComponent>(ent) ||
            Transform(ent).MapUid is not { } map || !HasComp<SalvageExpeditionComponent>(map))
            return;

        EnsureComp<ExpeditionMobComponent>(ent).Expedition = map;
    }

    private void OnInit(Entity<ExpeditionMobComponent> ent, ref ComponentInit args)
    {
        _meta.AddFlag(ent, MetaDataFlags.ExtraTransformEvents);
    }

    private void OnRemove(Entity<ExpeditionMobComponent> ent, ref ComponentRemove args)
    {
        _pending.Remove(ent);
        _meta.RemoveFlag(ent, MetaDataFlags.ExtraTransformEvents);
    }

    private void OnMetaFlagRemoveAttempt(Entity<ExpeditionMobComponent> ent, ref MetaFlagRemoveAttemptEvent args)
    {
        if (ent.Comp.LifeStage <= ComponentLifeStage.Running)
            args.ToRemove &= ~MetaDataFlags.ExtraTransformEvents;
    }

    private void OnMapChanged(Entity<ExpeditionMobComponent> ent, ref MapUidChangedEvent args)
    {
        // Transform events also reach children of moving grids/containers. Gibbing here would
        // modify the transform hierarchy while the original move is still being processed.
        _pending.Add(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_pending.Count == 0)
            return;

        var pending = _pending.ToArray();
        _pending.Clear();
        foreach (var uid in pending)
        {
            if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid) ||
                !TryComp<ExpeditionMobComponent>(uid, out var origin) ||
                Transform(uid).MapUid == origin.Expedition)
                continue;

            if (Transform(uid).MapUid != null)
            {
                if (TryComp<BodyComponent>(uid, out var body))
                    _body.GibBody(uid, gibOrgans: true, body: body);
                else
                {
                    // Bodyless NPCs also contain action and solution entities. Drop their
                    // physical contents, keeping those internal entities attached for deletion.
                    foreach (var container in _containers.GetAllContainers(uid))
                    {
                        foreach (var child in container.ContainedEntities.ToArray())
                        {
                            if (HasComp<PhysicsComponent>(child))
                                _transform.DropNextTo(child, (uid, Transform(uid)));
                        }
                    }

                    _gibbing.TryGibEntity(uid, (uid, EnsureComp<GibbableComponent>(uid)),
                        GibType.Gib, GibContentsOption.Skip, out _, launchGibs: false);
                }
            }

            // Also handles bodies moved into nullspace, where gib effects cannot be spawned.
            QueueDel(uid);
        }
    }
}
