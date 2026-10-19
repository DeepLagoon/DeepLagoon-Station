using Content.Client.UserInterface.Controls;
using Content.Client.VendingMachines.UI;
using Content.Shared.VendingMachines;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using System.Linq;
using Content.Client._Mono.Economy;
using Content.Shared._Mono.Economy.Components; // Mono
using Robust.Client.GameObjects;
using Content.Shared._NF.Bank.Components; // Frontier
using Content.Shared._Mono.Traits.Physical;
using Robust.Client.Player;

namespace Content.Client.VendingMachines
{
    public sealed partial class VendingMachineBoundUserInterface : BoundUserInterface
    {
        [Dependency] private CreditReceiverSystem _credit = default!; // Mono

        [ViewVariables]
        private VendingMachineMenu? _menu;

        [ViewVariables]
        private List<VendingMachineInventoryEntry> _cachedInventory = new();

        [ViewVariables]
        private float _mod = 1f;
        [ViewVariables]
        private int _balance = 0;
        [ViewVariables]
        private int _cashSlotBalance = 0;
        // End Frontier
        [ViewVariables]
        private bool _requiresCash; // mono

        public VendingMachineBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
        {
        }

        protected override void Open()
        {
            base.Open();

            if (OpenTgui())
            {
                Update();
                return;
            }

            // The engine registers one native control per BUI until the BUI closes.
            // Switching presentation must reuse it, including its event subscriptions.
            if (_menu != null)
            {
                _menu.Open(_menu.Position);
                Update();
                return;
            }

            _menu = this.CreateWindowCenteredLeft<VendingMachineMenu>();
        Content.Client._DeepLagoon.WebUI.WindowInterfaceStyle.Attach(_menu, "VendingMachine", () => false, next =>
        {
            Content.Client._DeepLagoon.WebUI.WindowInterfaceStyle.Save("VendingMachine", next);
            SwitchInterface(next);
        });
            // Frontier: no exceptions
            if (EntMan.TryGetComponent(Owner, out MetaDataComponent? meta))
                _menu.Title = meta.EntityName;
            else
                _menu.Title = Loc.GetString("vending-machine-nf-fallback-title");
            // End Frontier: no exceptions
            _menu.OnItemSelected += OnItemSelected;
            Update();
        }

        public override void Update()
        {
            var system = EntMan.System<VendingMachineSystem>();
            _cachedInventory = system.GetAllInventory(Owner);

            RefreshPaymentState();
            PushTguiState();

            _menu?.Populate(_cachedInventory, _mod, _balance, _cashSlotBalance, _requiresCash);
        }

        private void RefreshPaymentState()
        {
            _mod = EntMan.TryGetComponent<MarketModifierComponent>(Owner, out var market) ? market.Mod : 1f;
            _requiresCash = EntMan.TryGetComponent<VendingMachineComponent>(Owner, out var machine) && machine.RequiresCash;
            _balance = 0;
            _hasBankAccount = false;

            // Frontier: state, market modifier, balance status
            var player = IoCManager.Resolve<IPlayerManager>();
            if (player.LocalEntity is { } actor && !EntMan.HasComponent<IronmanComponent>(actor)
                && EntMan.TryGetComponent<BankAccountComponent>(actor, out var bank))
            {
                _balance = bank.Balance;
                _hasBankAccount = true;
            }
            _acceptsCash = EntMan.HasComponent<CreditReceiverComponent>(Owner);
            if (_acceptsCash)
            {
                _credit.TryGetCash(Owner, out _, out var cash);
                _cashSlotBalance = cash;
            }
            else
            {
                _cashSlotBalance = 0;
            }
            // End Frontier
        }

        private void OnItemSelected(GUIBoundKeyEventArgs args, ListData data)
        {
            if (args.Function != EngineKeyFunctions.UIClick)
                return;

            if (data is not VendorItemsListData { ItemIndex: var itemIndex })
                return;

            if (_cachedInventory.Count == 0)
                return;

            var selectedItem = _cachedInventory.ElementAtOrDefault(itemIndex);

            if (selectedItem == null)
                return;

            SendMessage(new VendingMachineEjectMessage(selectedItem.Type, selectedItem.ID));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CloseTguiWindow();

            base.Dispose(disposing);
            if (!disposing)
                return;

            if (_menu == null)
                return;

            _menu.OnItemSelected -= OnItemSelected;
            _menu.OnClose -= Close;
            _menu.Dispose();
        }
    }
}
