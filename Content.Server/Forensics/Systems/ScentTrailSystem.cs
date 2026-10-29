using Content.Shared.Forensics;
using Content.Shared.Forensics.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.Events;

namespace Content.Server.Forensics.Systems;

public sealed class ScentTrailSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<DnaComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<DnaComponent, GenerateDnaEvent>(OnDna);
        SubscribeLocalEvent<DnaComponent, ContactInteractionEvent>(OnContact);
        SubscribeLocalEvent<DnaComponent, DidEquipEvent>(OnEquip);
        SubscribeLocalEvent<ScentTrailComponent, CleanForensicsDoAfterEvent>(OnClean);
    }

    private void OnStartup(EntityUid uid, DnaComponent component, ComponentStartup args) => Seed(uid, component.DNA);
    private void OnDna(EntityUid uid, DnaComponent component, ref GenerateDnaEvent args) => Seed(uid, args.DNA);

    private void Seed(EntityUid uid, string dna)
    {
        var trail = EnsureComp<ScentTrailComponent>(uid);
        trail.Scent = dna.Length > 0 ? dna : uid.ToString();
        Dirty(uid, trail);
    }

    private void OnContact(EntityUid uid, DnaComponent component, ContactInteractionEvent args) => Apply(uid, args.Other);
    private void OnEquip(EntityUid uid, DnaComponent component, DidEquipEvent args) => Apply(uid, args.Equipment);

    private void Apply(EntityUid uid, EntityUid target)
    {
        if (HasComp<DnaComponent>(target) || !TryComp<ScentTrailComponent>(uid, out var source)) return;
        var trail = EnsureComp<ScentTrailComponent>(target);
        trail.Scent = source.Scent;
        Dirty(target, trail);
    }

    private void OnClean(EntityUid uid, ScentTrailComponent component, CleanForensicsDoAfterEvent args)
    {
        if (args.Cancelled || HasComp<DnaComponent>(uid)) return;
        component.Scent = "";
        Dirty(uid, component);
    }
}
