using Content.Server.EUI;
using Content.Shared.Eui;
using Content.Shared.Ghost.Roles;
using System.Linq;
using Content.Shared.Ghost;
using Robust.Server.GameStates;

namespace Content.Server.Ghost.Roles.UI
{
    public sealed class GhostRolesEui : BaseEui
    {
        private readonly GhostRoleSystem _ghostRoleSystem;
        private readonly IEntityManager _entities = IoCManager.Resolve<IEntityManager>();
        private readonly HashSet<EntityUid> _previews = new();

        public GhostRolesEui()
        {
            _ghostRoleSystem = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<GhostRoleSystem>();
        }

        public override GhostRolesEuiState GetNewState()
        {
            var roles = _ghostRoleSystem.GetGhostRolesInfo(Player);
            var available = roles.Where(role => role.PreviewEntity != null)
                .Select(role => _entities.GetEntity(role.PreviewEntity!.Value)).ToHashSet();
            SetPreviews(_previews.Where(available.Contains).ToHashSet());
            return new(roles);
        }

        public override void HandleMessage(EuiMessageBase msg)
        {
            base.HandleMessage(msg);

            switch (msg)
            {
                case GhostRolePreviewSubscription request:
                    if (request.Identifiers.Length > GhostRolePreviewSubscription.MaxRoles ||
                        !_entities.HasComponent<GhostComponent>(Player.AttachedEntity))
                        return;
                    var identifiers = request.Identifiers.ToHashSet();
                    // Never accept entity IDs supplied by a browser. Resolve only currently offered roles.
                    var wanted = _ghostRoleSystem.GetGhostRolesInfo(Player)
                        .Where(role => identifiers.Contains(role.Identifier) && role.PreviewEntity != null)
                        .Select(role => _entities.GetEntity(role.PreviewEntity!.Value)).ToHashSet();
                    SetPreviews(wanted);
                    break;
                case RequestGhostRoleMessage req:
                    _ghostRoleSystem.Request(Player, req.Identifier);
                    break;
                case FollowGhostRoleMessage req:
                    _ghostRoleSystem.Follow(Player, req.Identifier);
                    break;
                case LeaveGhostRoleRaffleMessage req:
                    _ghostRoleSystem.LeaveRaffle(Player, req.Identifier);
                    break;
            }
        }

        public override void Closed()
        {
            SetPreviews(new HashSet<EntityUid>());
            base.Closed();

            _ghostRoleSystem.CloseEui(Player);
        }

        private void SetPreviews(HashSet<EntityUid> wanted)
        {
            var pvs = _entities.System<PvsOverrideSystem>();
            foreach (var stale in _previews.Except(wanted))
                pvs.RemoveSessionOverride(stale, Player);
            foreach (var added in wanted.Except(_previews))
                pvs.AddSessionOverride(added, Player);
            _previews.Clear();
            _previews.UnionWith(wanted);
        }
    }
}
