using Content.Server.Power.NodeGroups;
using Content.Server.Power.Pow3r;
using Content.Shared.Guidebook;

namespace Content.Server.Power.Components
{
    [RegisterComponent]
    public sealed partial class PowerSupplierComponent : BaseNetConnectorComponent<IBasePowerNet>
    {
        private bool _enabled = true;
        private bool _locationBlocked;

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("supplyRate")]
        [GuidebookData]
        public float MaxSupply { get => NetworkSupply.MaxSupply; set => NetworkSupply.MaxSupply = value; }

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("supplyRampTolerance")]
        public float SupplyRampTolerance
        {
            get => NetworkSupply.SupplyRampTolerance;
            set => NetworkSupply.SupplyRampTolerance = value;
        }

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("supplyRampRate")]
        public float SupplyRampRate
        {
            get => NetworkSupply.SupplyRampRate;
            set => NetworkSupply.SupplyRampRate = value;
        }

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("supplyRampPosition")]
        public float SupplyRampPosition
        {
            get => NetworkSupply.SupplyRampPosition;
            set => NetworkSupply.SupplyRampPosition = value;
        }

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("enabled")]
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                NetworkSupply.Enabled = _enabled && !_locationBlocked;
            }
        }

        internal void SetLocationBlocked(bool blocked)
        {
            _locationBlocked = blocked;
            NetworkSupply.Enabled = _enabled && !_locationBlocked;
        }

        [ViewVariables] public float CurrentSupply => NetworkSupply.CurrentSupply;

        [ViewVariables]
        public PowerState.Supply NetworkSupply { get; } = new();

        protected override void AddSelfToNet(IBasePowerNet powerNet)
        {
            powerNet.AddSupplier(this);
        }

        protected override void RemoveSelfFromNet(IBasePowerNet powerNet)
        {
            powerNet.RemoveSupplier(this);
        }

        // Mono
        /// <summary>
        ///     If we have a <see cref="ThermalSignatureComponent">, heat signature generated per watt currently supplied.
        /// </summary>
        [DataField]
        public float HeatSignatureRatio = 4f;
    }
}
