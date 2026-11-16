using Content.Client._DeepLagoon.WebUI;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private bool _backpackEditorOpen;

    private void CloseBackpackEditor()
    {
        _backpackEditorOpen = false;
        _personalLoadoutEditor.CloseBackpackPreview();
    }

    private bool HandleBackpackEditorAction(string action, TguiActionData args)
    {
        if (action == "backpack-close")
        {
            CloseBackpackEditor();
            return true;
        }
        if (_tguiMode != "equipment")
            return false;
        if (action == "backpack-open")
        {
            _backpackEditorOpen = true;
            return true;
        }
        return _backpackEditorOpen && action is ("backpack-add" or "backpack-move" or "backpack-remove") &&
            _personalLoadoutEditor.HandleTguiBackpackAction(action, args);
    }
}
