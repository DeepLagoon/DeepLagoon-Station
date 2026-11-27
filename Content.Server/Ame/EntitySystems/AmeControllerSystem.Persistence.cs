using Content.Server.Ame.Components;

namespace Content.Server.Ame.EntitySystems;

public sealed partial class AmeControllerSystem
{
    internal void RestoreAfterLoad(EntityUid uid, AmeControllerComponent controller)
    {
        var now = _gameTiming.CurTime;
        // Legacy snapshots contained absolute round times. Never wait for the old round's clock,
        // or inject fuel for all the time the ship spent unloaded.
        if (controller.NextUpdate < now || controller.NextUpdate > now + controller.UpdatePeriod)
            controller.NextUpdate = now + controller.UpdatePeriod;
        controller.LastUpdate = now;
        controller.NextUIUpdate = now;
        UpdateUi(uid, controller);
    }
}
