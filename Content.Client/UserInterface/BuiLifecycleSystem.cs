using Robust.Client.GameObjects;
using Robust.Client.GameStates;

namespace Content.Client.UserInterface;

/// <summary>
/// Keeps server-authorized UI opens and closes running when simulation prediction is disabled.
/// Uses the official engine's public lifecycle API, without predicting gameplay or replaying input.
/// </summary>
public sealed class BuiLifecycleSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IClientGameStateManager _states = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesOutsidePrediction = true;
        UpdatesAfter.Add(typeof(UserInterfaceSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Newer engines may opt in themselves. Never process the queue twice.
        if (!_states.IsPredictionEnabled && !_ui.UpdatesOutsidePrediction)
            _ui.Update(frameTime);
    }
}
