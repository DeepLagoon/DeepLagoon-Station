using Content.Client.UserInterface.Controls;
using Content.Shared.SmartFridge;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.GameObjects;

namespace Content.Client.SmartFridge;

public sealed class SmartFridgeBoundUserInterface : BoundUserInterface
{
    private SmartFridgeMenu? _menu;

    public SmartFridgeBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<SmartFridgeMenu>();
        _menu.OnItemSelected += OnItemSelected;
        _menu.OnRemoveButtonPressed += data => SendPredictedMessage(new SmartFridgeRemoveEntryMessage(data.Entry));
        Refresh();
    }
    public void Refresh()
    {
        if (_menu is not { } menu ||
            !EntMan.TryGetComponent(Owner, out SmartFridgeComponent? fridge))
            return;

        // Use the localized name of the actual machine.
        menu.Title = EntMan.GetComponent<MetaDataComponent>(Owner).EntityName;

        menu.SetFlavorText(Loc.GetString(fridge.FlavorText));
        menu.Populate((Owner, fridge));
    }

    private void OnItemSelected(GUIBoundKeyEventArgs args, ListData data)
    {
        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        if (data is not SmartFridgeListData entry)
            return;
        SendPredictedMessage(new SmartFridgeDispenseItemMessage(entry.Entry));
    }
}
