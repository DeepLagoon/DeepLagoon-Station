using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Lathe.UI
{
    [UsedImplicitly]
    public sealed partial class LatheBoundUserInterface : BoundUserInterface // DeepLagoon: partial, see _DeepLagoon/Lathe
    {
        [ViewVariables]
        private LatheMenu? _menu;
        public LatheBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
        {
        }

        protected override void Open()
        {
            base.Open();

            if (OpenTgui()) // DeepLagoon: TGUI menu, this one is the fallback
                return;

            // The engine registers one native control per BUI until the BUI closes.
            // Switching presentation must reuse it, including its event subscriptions.
            if (_menu != null)
            {
                _menu.Open(_menu.Position);
                if (State != null) UpdateState(State);
                return;
            }

            _menu = this.CreateWindowCenteredRight<LatheMenu>();
        Content.Client._DeepLagoon.WebUI.WindowInterfaceStyle.Attach(_menu, "LatheConsole", () => false, next =>
        {
            Content.Client._DeepLagoon.WebUI.WindowInterfaceStyle.Save("LatheConsole", next);
            SwitchInterface(next);
        });
            _menu.SetEntity(Owner);

            _menu.OnServerListButtonPressed += _ =>
            {
                SendMessage(new ConsoleServerSelectionMessage());
            };

            _menu.RecipeQueueAction += (recipe, amount) =>
            {
                SendMessage(new LatheQueueRecipeMessage(recipe, amount));
            };

            // <Mono>
            _menu.OnLoopCheckboxPressed += (loop) => SendMessage(new LatheSetLoopingMessage(loop));
            _menu.OnSkipCheckboxPressed += (skip) => SendMessage(new LatheSetSkipMessage(skip));
            _menu.OnRecipeCancelled += (index) => SendMessage(new LatheRecipeCancelMessage(index));
            // </Mono>
        }

        protected override void UpdateState(BoundUserInterfaceState state)
        {
            base.UpdateState(state);

            if (UpdateTgui(state)) // DeepLagoon
                return;

            switch (state)
            {
                case LatheUpdateState msg:
                    if (_menu != null)
                        _menu.Recipes = msg.Recipes;
                    _menu?.PopulateRecipes();
                    _menu?.UpdateCategories();
                    _menu?.PopulateQueueList(msg.Queue);
                    _menu?.SetQueueInfo(msg.CurrentlyProducing);
                    _menu?.SetLooping(msg.Looping); // Mono
                    _menu?.SetSkipping(msg.Skipping); // Mono
                    break;
            }
        }
    }
}
