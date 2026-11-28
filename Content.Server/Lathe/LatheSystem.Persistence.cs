using Content.Server.Lathe.Components;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;

namespace Content.Server.Lathe;

public sealed partial class LatheSystem
{
    /// <summary>Reconstruct runtime production without charging an already-paid recipe again.</summary>
    internal void RestoreAfterLoad(EntityUid uid, LatheComponent component)
    {
        component.CurrentRecipe = null;
        if (TryComp<LatheProducingComponent>(uid, out var producing))
        {
            if (producing.Recipe != null && _proto.TryIndex<LatheRecipePrototype>(producing.Recipe, out var recipe))
                component.CurrentRecipe = recipe;
            else
            {
                // Old snapshots saved the active marker but neither recipe nor duration.
                // It cannot be reconstructed safely; free the machine rather than lock its queue forever.
                RemComp<LatheProducingComponent>(uid);
            }
        }

        _appearance.SetData(uid, LatheVisuals.IsInserting, false);
        UpdateRunningAppearance(uid, component.CurrentRecipe != null);
        _materialStorage.UpdateMaterialWhitelist(uid);
        _deviceLink.EnsureSinkPorts(uid, component.PausePort, component.ResumePort);
        _deviceLink.EnsureSourcePorts(uid, component.ProducedPort);
        UpdateUserInterfaceState(uid, component);
    }
}
