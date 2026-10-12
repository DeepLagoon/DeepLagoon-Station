using Content.Shared.Research.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>
/// Sprite frames and lathe recipe icons shared by every TGUI window of the session, so the research
/// console and the lathes decode each image only once.
/// </summary>
public static class TguiRecipeIcons
{
    public static TguiSpriteImages Images => TguiSpriteImages.Shared;

    /// <summary>Drops every decoded frame, after the prototypes were reloaded.</summary>
    public static void Reset() => TguiSpriteImages.ResetShared();

    /// <summary>The layers of a recipe's icon: its own icon, otherwise its result entity.</summary>
    public static IEnumerable<TguiData> Layers(LatheRecipePrototype recipe)
    {
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        switch (recipe.Icon)
        {
            case SpriteSpecifier.EntityPrototype entity when prototypes.HasIndex<EntityPrototype>(entity.EntityPrototypeId):
                return Images.Item(entity.EntityPrototypeId);
            case { } icon:
                return [new TguiData().String("url", Images.Frame(icon)).String("color", "#ffffff")];
        }

        if (recipe.Result is { } result && prototypes.HasIndex<EntityPrototype>(result))
            return Images.Item(result);
        return [];
    }
}
