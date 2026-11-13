using System.Linq;
using Content.Shared.Interaction;
using Content.Server.Popups;
using Content.Shared.Research.Prototypes;
using Content.Server.Research.Systems;
using Content.Shared.Research.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.Research.Disk
{
    public sealed partial class ResearchDiskSystem : EntitySystem
    {
        [Dependency] private IPrototypeManager _prototype = default!;
        [Dependency] private PopupSystem _popupSystem = default!;
        [Dependency] private ResearchSystem _research = default!;
        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<ResearchDiskComponent, AfterInteractEvent>(OnAfterInteract);
            SubscribeLocalEvent<ResearchDiskComponent, MapInitEvent>(OnMapInit);
        }

        private void OnAfterInteract(EntityUid uid, ResearchDiskComponent component, AfterInteractEvent args)
        {
            if (args.Handled || !args.CanReach)
                return;

            if (!TryComp<ResearchServerComponent>(args.Target, out var server))
                return;

            args.Handled = TryUpload((uid, component), (args.Target.Value, server), args.User);
        }

        public bool TryUpload(Entity<ResearchDiskComponent> disk, Entity<ResearchServerComponent> server, EntityUid? user)
        {
            if (TerminatingOrDeleted(server) || !TryQueueDel(disk))
                return false;

            _research.ModifyServerPoints(server, disk.Comp.Points, server.Comp);
            var message = Loc.GetString("research-disk-inserted", ("points", disk.Comp.Points));
            if (user is { } uid)
                _popupSystem.PopupEntity(message, server, uid);
            else
                _popupSystem.PopupEntity(message, server);
            return true;
        }

        private void OnMapInit(EntityUid uid, ResearchDiskComponent component, MapInitEvent args)
        {
            if (!component.UnlockAllTech)
                return;

            component.Points = _prototype.EnumeratePrototypes<TechnologyPrototype>()
                .Sum(tech => tech.Cost);
        }
    }
}
