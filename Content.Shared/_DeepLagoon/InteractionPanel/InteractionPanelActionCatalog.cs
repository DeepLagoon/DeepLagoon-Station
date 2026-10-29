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

[Prototype]
public sealed partial class InteractionPanelPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int Order;
    [DataField(required: true)] public string Body = default!;
    [DataField(required: true)] public InteractionPanelCategory Category;
    [DataField(required: true)] public InteractionPanelActionFlags[] Flags = default!;
    // Male/Female match the selected Sex or enabled primary body features. Unsexed matches the selected Sex.
    [DataField] public Sex[]? UserSexes;
    [DataField] public Sex[]? TargetSexes;
    [DataField] public string? Sound;
    // При наличии sounds выбирается один ID из списка; пустой список отключает звук.
    [DataField] public string[]? Sounds;
    [DataField] public string[]? Messages;
    [DataField] public string[]? SelfMessages;
    [DataField] public float ArousalGain;
    [DataField] public float? UserArousalGain;
    [DataField] public float? TargetArousalGain;
    [DataField] public string? UserBodyPart;
    [DataField] public string? TargetBodyPart;
    [DataField] public string? UserFluidDestination;
    [DataField] public string? TargetFluidDestination;
    [DataField] public bool RequiresRecovered;
    [DataField] public float? RawArousalLimit;
    // Keep existing custom catalogs compatible.
    [DataField("manaGain")] public float LegacyManaGain;
    [DataField] public string? Effect;
    [DataField] public InteractionPanelAnimationSettings? Animation;
    [DataField(required: true)] public Color ChatColor;

    public InteractionPanelActionDefinition Definition() => new(ID, Body, Category,
        Flags.Aggregate(InteractionPanelActionFlags.None, (all, flag) => all | flag),
        Sound, Effect, Animation, ChatColor.ToHex(), UserSexes: UserSexes, TargetSexes: TargetSexes, ArousalGain: ArousalGain != 0 ? ArousalGain : LegacyManaGain, Sounds: Sounds, Messages: Messages, SelfMessages: SelfMessages,
        UserArousalGain: UserArousalGain, TargetArousalGain: TargetArousalGain, UserBodyPart: UserBodyPart, TargetBodyPart: TargetBodyPart,
        UserFluidDestination: UserFluidDestination, TargetFluidDestination: TargetFluidDestination, RequiresRecovered: RequiresRecovered, RawArousalLimit: RawArousalLimit);
}
