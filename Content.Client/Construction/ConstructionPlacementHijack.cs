using System.Linq;
using Content.Client.Atmos;
using Content.Shared.Construction.Prototypes;
using Robust.Client.Placement;
using Robust.Client.Placement.Modes;
using Robust.Client.Utility;
using Robust.Shared.Map;

namespace Content.Client.Construction
{
    public sealed class ConstructionPlacementHijack : PlacementHijack
    {
        private readonly ConstructionSystem _constructionSystem;
        public ConstructionPrototype? CurrentPrototype { get; private set; }
        public override bool CanRotate => CurrentPrototype?.CanRotate ?? true;

        public ConstructionPlacementHijack(ConstructionSystem constructionSystem, ConstructionPrototype? prototype)
        {
            _constructionSystem = constructionSystem;
            CurrentPrototype = prototype;
        }

        /// <inheritdoc />
        public override bool HijackPlacementRequest(EntityCoordinates coordinates)
        {
            if (CurrentPrototype != null)
            {
                var dir = Manager.Direction;
                _constructionSystem.SpawnGhost(CurrentPrototype, coordinates, dir);
            }
            return true;
        }

        /// <inheritdoc />
        public override bool HijackDeletion(EntityUid entity)
        {
            if (IoCManager.Resolve<IEntityManager>().HasComponent<ConstructionGhostComponent>(entity))
            {
                _constructionSystem.ClearGhost(entity.GetHashCode());
            }
            return true;
        }

        /// <inheritdoc />
        public override void StartHijack(PlacementManager manager)
        {
            base.StartHijack(manager);
            manager.CurrentTextures = CurrentPrototype?.Layers.Select(sprite => sprite.DirFrame0()).ToList();

            if (manager.CurrentMode is { } mode &&
                CurrentPrototype?.PlacementMode is nameof(PlaceFree) or nameof(SnapgridCenter) or nameof(AlignAtmosPipeLayers))
            {
                manager.CurrentMode = new ConstructionPlacementMode(manager, mode, this);
            }
        }

        internal void SetPrototype(ConstructionPrototype prototype)
        {
            if (CurrentPrototype == prototype)
                return;

            CurrentPrototype = prototype;
            Manager.CurrentTextures = prototype.Layers.Select(sprite => sprite.DirFrame0()).ToList();
            _constructionSystem.GetGuide(prototype);
        }
    }
}
