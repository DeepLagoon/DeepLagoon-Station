using Robust.Shared.Prototypes;
using System.Numerics;
using Content.Server.Shuttles.Systems;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Damage;
using Content.Shared.DeviceLinking; // Frontier
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Shuttles.Components
{
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentPause]
    [Access(typeof(ThrusterSystem))]
    public sealed partial class ThrusterComponent : Component
    {
        /// <summary>
        /// Whether the thruster has been force to be enabled / disabled (e.g. VV, interaction, etc.)
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// This determines whether the thruster is actually enabled for the purposes of thrust
        /// </summary>
        public bool IsOn;

        // Need to serialize this because RefreshParts isn't called on Init and this will break post-mapinit maps!
        [ViewVariables(VVAccess.ReadWrite), DataField("thrust")]
        public float Thrust = 100f;

        [DataField("baseThrust"), ViewVariables(VVAccess.ReadWrite)]
        public float BaseThrust = 100f;

        [DataField("thrusterType")]
        public ThrusterType Type = ThrusterType.Linear;

        /// <summary>
        /// Exhaust damage area in local coordinates, extending behind the nozzle.
        /// Larger engine prototypes override this with a wider, longer plume.
        /// </summary>
        [DataField("burnShape")] public List<Vector2> BurnPoly = new()
        {
            new Vector2(-0.4f, 0.5f),
            new Vector2(-0.25f, 1.4f),
            new Vector2(0.25f, 1.4f),
            new Vector2(0.4f, 0.5f)
        };

        /// <summary>
        /// Damage applied to entities in the exhaust on each fire cooldown.
        /// </summary>
        [DataField("damage")] public DamageSpecifier? Damage = new();

        [DataField("requireSpace")]
        public bool RequireSpace = true;

        public bool Firing = false;

        /// <summary>
        /// How often thruster deals damage.
        /// </summary>
        [DataField]
        public TimeSpan FireCooldown = TimeSpan.FromSeconds(2);

        [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
        public TimeSpan NextFire = TimeSpan.Zero;

        // Frontier: upgradeable parts, togglable thrust
        [DataField("machinePartThrust")]
        public ProtoId<MachinePartPrototype> MachinePartThrust = "Capacitor";

        [DataField("partRatingThrustMultiplier")]
        public float PartRatingThrustMultiplier = 1.25f; // Mono: 1.15->1.25 (frontier: 1.5->1.15)

        /// <summary>
        ///     Frontier - Amount of charge this needs from an APC per second to function.
        /// </summary>
        public float OriginalLoad { get; set; } = 0;

        /// <summary>
        ///     Frontier - Make linkable to buttons
        /// </summary>
        [DataField("onPort")] // Frontier
        public ProtoId<SinkPortPrototype> OnPort = "On"; // Frontier

        [DataField("offPort")] // Frontier
        public ProtoId<SinkPortPrototype> OffPort = "Off"; // Frontier

        [DataField("togglePort")] // Frontier
        public ProtoId<SinkPortPrototype> TogglePort = "Toggle"; // Frontier
        // End Frontier: upgradeable parts, togglable thrust

        // Mono
        /// <summary>
        ///     If we have a <see cref="ThermalSignatureComponent">, heat signature output per thrust while working.
        /// </summary>
        [DataField]
        public float HeatSignatureRatio = 40f;
    }

    public enum ThrusterType
    {
        Linear,
        // Angular meaning rotational.
        Angular,
    }
}
