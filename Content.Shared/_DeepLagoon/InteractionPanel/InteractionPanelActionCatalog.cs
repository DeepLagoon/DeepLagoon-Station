using System.Linq;
using Content.Shared.Humanoid;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.InteractionPanel;

// Только загрузка. Все определения действий находятся в interaction_panel_actions.yml.
public static class InteractionPanelActionCatalog
{
    public static InteractionPanelActionDefinition[] Load(IPrototypeManager prototypes) =>
        prototypes.EnumeratePrototypes<InteractionPanelPrototype>()
            .OrderBy(p => p.Order).ThenBy(p => p.ID, Comparer<string>.Create(string.CompareOrdinal))
            .Select(p => p.Definition()).ToArray();
}

[Prototype("interactionPanel")]
public sealed partial class InteractionPanelPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int Order;
    [DataField(required: true)] public string Body = default!;
    [DataField(required: true)] public InteractionPanelCategory Category;
    [DataField(required: true)] public InteractionPanelActionFlags[] Flags = default!;
    // Необязательно. Значения: Male, Female, Unsexed. Это Sex тела, не Gender местоимений.
    [DataField] public Sex[]? UserSexes;
    [DataField] public Sex[]? TargetSexes;
    [DataField] public string? Sound;
    // При наличии sounds выбирается один ID из списка; пустой список отключает звук.
    [DataField] public string[]? Sounds;
    [DataField] public float ManaGain;
    [DataField] public string? Effect;
    [DataField] public InteractionPanelAnimationSettings? Animation;
    [DataField(required: true)] public Color ChatColor;

    public InteractionPanelActionDefinition Definition() => new(ID, Body, Category,
        Flags.Aggregate(InteractionPanelActionFlags.None, (all, flag) => all | flag),
        Sound, Effect, Animation, ChatColor.ToHex(), UserSexes: UserSexes, TargetSexes: TargetSexes, ManaGain: ManaGain, Sounds: Sounds);
}

