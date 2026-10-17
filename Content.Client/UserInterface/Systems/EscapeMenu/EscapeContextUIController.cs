using Content.Client.UserInterface.Systems.Info;
using Content.Shared.Input;
using Content.Client.Lobby;
using JetBrains.Annotations;
using Robust.Client.Input;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;

namespace Content.Client.UserInterface.Systems.EscapeMenu;

[UsedImplicitly]
public sealed partial class EscapeContextUIController : UIController
{
    [Dependency] private IInputManager _inputManager = default!;
    [Dependency] private IStateManager _stateManager = default!;

    [Dependency] private CloseRecentWindowUIController _closeRecentWindowUIController = default!;
    [Dependency] private EscapeUIController _escapeUIController = default!;
    [Dependency] private OptionsUIController _optionsUIController = default!;

    public override void Initialize()
    {
        _inputManager.SetInputCommand(ContentKeyFunctions.EscapeContext,
            InputCmdHandler.FromDelegate(_ => CloseWindowOrOpenGameMenu()));
    }

    private void CloseWindowOrOpenGameMenu()
    {
        if (_closeRecentWindowUIController.HasClosableWindow())
        {
            _closeRecentWindowUIController.CloseMostRecentWindow();
        }
        else if (_stateManager.CurrentState is LobbyState)
        {
            _optionsUIController.OpenWindow();
        }
        else
        {
            _escapeUIController.ToggleWindow();
        }
    }
}
