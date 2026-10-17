using Content.Shared.Eui;
using Content.Shared.Roles;
using Robust.Shared.Prototypes; // Frontier
using Robust.Shared.Serialization;

namespace Content.Shared.Ghost.Roles
{
    [NetSerializable, Serializable]
    public struct GhostRoleInfo
    {
        public uint Identifier { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Rules { get; set; }
        public ProtoId<GhostRolePrototype>? Prototype { get; set; } // Frontier: store GhostRolePrototype for whitelist lookup

        /// <summary>The offered entity, not its catalogue icon. Null for roles that spawn a new mob.</summary>
        public NetEntity? PreviewEntity { get; set; }

        /// <summary>Target mob of a spawner. Its final randomized appearance does not exist yet.</summary>
        public EntProtoId? PreviewPrototype { get; set; }

        // TODO ROLE TIMERS
        // Actually make use of / enforce this requirement?
        // Why is this even here.
        // Move to ghost role prototype & respect CCvars.GameRoleTimerOverride
        public HashSet<JobRequirement>? Requirements { get; set; }

        /// <inheritdoc cref="GhostRoleKind"/>
        public GhostRoleKind Kind { get; set; }

        /// <summary>
        /// if <see cref="Kind"/> is <see cref="GhostRoleKind.RaffleInProgress"/>, specifies how many players are currently
        /// in the raffle for this role.
        /// </summary>
        public uint RafflePlayerCount { get; set; }

        /// <summary>
        /// if <see cref="Kind"/> is <see cref="GhostRoleKind.RaffleInProgress"/>, specifies when raffle finishes.
        /// </summary>
        public TimeSpan RaffleEndTime { get; set; }

    }

    [NetSerializable, Serializable]
    public sealed class GhostRolesEuiState : EuiStateBase
    {
        public GhostRoleInfo[] GhostRoles { get; }

        public GhostRolesEuiState(GhostRoleInfo[] ghostRoles)
        {
            GhostRoles = ghostRoles;
        }
    }

    [NetSerializable, Serializable]
    public sealed class RequestGhostRoleMessage : EuiMessageBase
    {
        public uint Identifier { get; }

        public RequestGhostRoleMessage(uint identifier)
        {
            Identifier = identifier;
        }
    }

    [NetSerializable, Serializable]
    public sealed class FollowGhostRoleMessage : EuiMessageBase
    {
        public uint Identifier { get; }

        public FollowGhostRoleMessage(uint identifier)
        {
            Identifier = identifier;
        }
    }

    [NetSerializable, Serializable]
    public sealed class LeaveGhostRoleRaffleMessage : EuiMessageBase
    {
        public uint Identifier { get; }

        public LeaveGhostRoleRaffleMessage(uint identifier)
        {
            Identifier = identifier;
        }
    }

    /// <summary>Only visible thumbnails need entities outside the ghost's current PVS.</summary>
    [NetSerializable, Serializable]
    public sealed class GhostRolePreviewSubscription : EuiMessageBase
    {
        public const int MaxRoles = 32;
        public uint[] Identifiers { get; }
        public GhostRolePreviewSubscription(uint[] identifiers) => Identifiers = identifiers;
    }

    /// <summary>
    /// Determines whether a ghost role is a raffle role, and if it is, whether it's running.
    /// </summary>
    [NetSerializable, Serializable]
    public enum GhostRoleKind
    {
        /// <summary>
        /// Role is not a raffle role and can be taken immediately.
        /// </summary>
        FirstComeFirstServe,

        /// <summary>
        /// Role is a raffle role, but raffle hasn't started yet.
        /// </summary>
        RaffleReady,

        /// <summary>
        ///  Role is raffle role and currently being raffled, but player hasn't joined raffle.
        /// </summary>
        RaffleInProgress,

        /// <summary>
        /// Role is raffle role and currently being raffled, and player joined raffle.
        /// </summary>
        RaffleJoined
    }
}
