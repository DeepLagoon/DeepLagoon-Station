using Content.Server.Body.Systems;
using Content.Server.Body.Components;
using Content.Server._DeepLagoon.Salvage;
using Content.Server.Cargo.Components;
using Content.Shared.Body.Components;
using Content.Shared.Body.Events;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;

namespace Content.Server.Salvage;

/// <summary>Expedition anatomy must never become a new source of NPC export payments.</summary>
public sealed class ExpeditionSalvageSystem : EntitySystem
{
    [Dependency] private BodySystem _bodies = default!;

    public override void Initialize()
    {
        // Reuse the same provenance as the expedition exit cleanup, including non-hostile,
        // factionless and deferred spawns aboard shuttles. Body map-init may happen later.
        SubscribeLocalEvent<ExpeditionMobComponent, ComponentStartup>(OnDefenderStartup);
        SubscribeLocalEvent<ExpeditionMobComponent, MapInitEvent>(OnDefenderMapInit, after: [typeof(BodySystem)]);
        SubscribeLocalEvent<OrganComponent, OrganAddedToBodyEvent>(OnOrganAdded);
        SubscribeLocalEvent<OrganComponent, OrganRemovedFromBodyEvent>(OnOrganRemoved);
        SubscribeLocalEvent<ExpeditionSalvageComponent, BodyPartAddedEvent>(OnPartAdded);
        SubscribeLocalEvent<ExpeditionSalvageComponent, BodyPartRemovedEvent>(OnPartRemoved);
        SubscribeLocalEvent<ExpeditionSalvageComponent, BeingGibbedEvent>(OnGibbed);
    }

    private void OnDefenderStartup(Entity<ExpeditionMobComponent> ent, ref ComponentStartup args) => ProtectDefender(ent);

    private void OnDefenderMapInit(Entity<ExpeditionMobComponent> ent, ref MapInitEvent args) => ProtectDefender(ent);

    public void ProtectDefender(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid)) return;
        Protect(uid);
        foreach (var part in _bodies.GetBodyChildren(uid))
            Protect(part.Id);
        foreach (var organ in _bodies.GetBodyOrgans(uid))
            Protect(organ.Id);
    }

    private void Protect(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid)) return;
        EnsureComp<ExpeditionSalvageComponent>(uid);
        EnsureComp<CargoSellBlacklistComponent>(uid);
    }

    public void RecordButcherSource(EntityUid machine, EntityUid victim)
    {
        if (HasComp<ExpeditionSalvageComponent>(victim)) EnsureComp<ExpeditionButcherSourceComponent>(machine);
        else RemComp<ExpeditionButcherSourceComponent>(machine);
    }

    public void ProtectButcherProduct(EntityUid source, EntityUid product)
    {
        if (HasComp<ExpeditionSalvageComponent>(source) || HasComp<ExpeditionButcherSourceComponent>(source))
            Protect(product);
    }

    private void OnGibbed(Entity<ExpeditionSalvageComponent> ent, ref BeingGibbedEvent args)
    {
        foreach (var gib in args.GibbedParts)
            if (HasComp<OrganComponent>(gib) || HasComp<BodyPartComponent>(gib))
                Protect(gib);
    }

    private void ProtectPart(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid)) return;
        // Only anatomy, never the inventory/equipment attached to the part.
        foreach (var part in _bodies.GetBodyPartChildren(uid))
        {
            Protect(part.Id);
            foreach (var organ in _bodies.GetPartOrgans(part.Id, part.Component))
                Protect(organ.Id);
        }
    }

    private void OnOrganAdded(Entity<OrganComponent> ent, ref OrganAddedToBodyEvent args)
    {
        if (HasComp<ExpeditionSalvageComponent>(args.Body) || HasComp<ExpeditionSalvageComponent>(args.Part))
            Protect(ent);
    }

    private void OnOrganRemoved(Entity<OrganComponent> ent, ref OrganRemovedFromBodyEvent args)
    {
        if (HasComp<ExpeditionSalvageComponent>(args.OldBody) || HasComp<ExpeditionSalvageComponent>(args.OldPart))
            Protect(ent);
    }

    private void OnPartAdded(Entity<ExpeditionSalvageComponent> ent, ref BodyPartAddedEvent args)
    {
        ProtectPart(args.Part);
    }

    private void OnPartRemoved(Entity<ExpeditionSalvageComponent> ent, ref BodyPartRemovedEvent args)
    {
        ProtectPart(args.Part);
    }

}
