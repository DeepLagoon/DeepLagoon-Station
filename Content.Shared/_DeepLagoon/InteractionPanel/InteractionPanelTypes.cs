using Content.Shared.Preferences;
using Content.Shared.Humanoid;
using System.Linq;
using Robust.Shared.Serialization;
using Robust.Shared.Random;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.DoAfter;

namespace Content.Shared._DeepLagoon.InteractionPanel;

[Serializable, NetSerializable]
public enum InteractionPanelConsent : byte { No, Ask, Yes }

[Serializable, NetSerializable]
public enum InteractionPanelCategory : byte { Erotic, NonCon, Vore, Neutral }

[Flags]
public enum InteractionPanelActionFlags : byte
{
    None = 0,
    INTERACTION_SELF = 1,
    INTERACTION_OTHER = 2,
    INTERACTION_HUMANOID = 4,
    INTERACTION_NON_HUMANOID = 8,
}

public sealed record InteractionPanelActionDefinition(string Id, string Body, InteractionPanelCategory Category,
    InteractionPanelActionFlags Flags = InteractionPanelActionFlags.INTERACTION_OTHER | InteractionPanelActionFlags.INTERACTION_HUMANOID,
    string? Sound = null, string? Effect = null, InteractionPanelAnimationSettings? Animation = null, string ChatColor = "#C6E8DA", string? Title = null, string? Template = null, Sex[]? UserSexes = null, Sex[]? TargetSexes = null, float ArousalGain = 0, string[]? Sounds = null, string[]? Messages = null, string[]? SelfMessages = null,
    float? UserArousalGain = null, float? TargetArousalGain = null, string? UserBodyPart = null, string? TargetBodyPart = null,
    string? UserFluidDestination = null, string? TargetFluidDestination = null, bool RequiresRecovered = false, float? RawArousalLimit = null)
{
    public bool HasMessageVariants(bool self) => (self ? SelfMessages : Messages) is { Length: > 0 };

    public string MessageKey(bool self, IRobustRandom random)
    {
        var messages = self ? SelfMessages : Messages;
        return messages is { Length: > 0 } ? random.Pick(messages)
            : $"dl-interaction-panel-{(self ? "self" : "result")}-{Id}";
    }

    // An omitted restriction allows any body; an empty restriction allows none.
    public bool SupportsSexes(Sex? user, Sex? target) =>
        SupportsSexTraits(InteractionPanelSexRules.GetTraits(user), InteractionPanelSexRules.GetTraits(target));

    public bool SupportsSexTraits(InteractionPanelSexTraits user, InteractionPanelSexTraits target) =>
        Matches(UserSexes, user) && Matches(TargetSexes, target);

    private static bool Matches(Sex[]? restriction, InteractionPanelSexTraits traits) =>
        restriction == null || restriction.Any(sex => (InteractionPanelSexRules.GetTraits(sex) & traits) != 0);

    public bool Supports(bool self, bool humanoid = true) =>
        (Flags & (self ? InteractionPanelActionFlags.INTERACTION_SELF : InteractionPanelActionFlags.INTERACTION_OTHER)) != 0 &&
        (Flags & (humanoid ? InteractionPanelActionFlags.INTERACTION_HUMANOID : InteractionPanelActionFlags.INTERACTION_NON_HUMANOID)) != 0;
}

// Каталог действий: InteractionPanelActionCatalog.cs. Настройки движения: InteractionPanelAnimationSettings.cs.
public static class InteractionPanelActions
{
    public const float MinInterval = 1f;
    public const float MaxInterval = 10f;
    public static float ClampInterval(float value) => float.IsFinite(value) ? MathF.Round(Math.Clamp(value, MinInterval, MaxInterval), 1) : 3f;


    public static bool DebugConsentOverride =>
#if DEEPLAGOON_INTERACTION_PANEL_DEBUG
        true;
#else
        false;
#endif
}

[ByRefEvent]
public readonly record struct InteractionPanelProfileLoadedEvent(HumanoidCharacterProfile Profile);

[Serializable, NetSerializable]
public sealed class InteractionPanelOpenEvent(NetEntity target) : EntityEventArgs
{
    public NetEntity Target = target;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelPanelEvent(NetEntity target, string name, string[] actions, bool self = false, bool debugPreferences = false, bool humanoid = true, Sex? userSex = null, Sex? targetSex = null, InteractionPanelSexTraits? userSexTraits = null, InteractionPanelSexTraits? targetSexTraits = null) : EntityEventArgs
{
    public NetEntity Target = target;
    public string Name = name;
    public string[] Actions = actions;
    public bool Self = self;
    public bool DebugPreferences = debugPreferences;
    public bool Humanoid = humanoid;
    public Sex? UserSex = userSex;
    public Sex? TargetSex = targetSex;
    public InteractionPanelSexTraits UserSexTraits = userSexTraits ?? InteractionPanelSexRules.GetTraits(userSex);
    public InteractionPanelSexTraits TargetSexTraits = targetSexTraits ?? InteractionPanelSexRules.GetTraits(targetSex);
}

[Serializable, NetSerializable]
public sealed class InteractionPanelAvailabilityEvent(NetEntity target, string[] actions, InteractionPanelSexTraits userSexTraits, InteractionPanelSexTraits targetSexTraits) : EntityEventArgs
{
    public NetEntity Target = target;
    public string[] Actions = actions;
    public InteractionPanelSexTraits UserSexTraits = userSexTraits;
    public InteractionPanelSexTraits TargetSexTraits = targetSexTraits;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelActionEvent(NetEntity target, string action) : EntityEventArgs
{
    public NetEntity Target = target;
    public string Action = action;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelAskEvent(Guid token, string name, string action, string? title = null, string? text = null) : EntityEventArgs
{
    public Guid Token = token;
    public string Name = name;
    public string Action = action;
    public string? Title = title;
    public string? Text = text;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelAnswerEvent(Guid token, bool accepted, bool block = false) : EntityEventArgs
{
    public Guid Token = token;
    public bool Accepted = accepted;
    public bool Block = block;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelRepeatEvent(NetEntity target, string action, bool enabled, float interval) : EntityEventArgs
{
    public NetEntity Target = target;
    public string Action = action;
    public bool Enabled = enabled;
    public float Interval = interval;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelCloseEvent(NetEntity target) : EntityEventArgs
{
    public NetEntity Target = target;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelBodyVisibilityEvent(string part, int visibility) : EntityEventArgs
{
    public string Part = part;
    public int Visibility = visibility;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelClimaxRequestEvent(NetEntity target, string part, string destination, bool container = false, bool clearPregnancy = false) : EntityEventArgs
{
    public NetEntity Target = target;
    public string Part = part;
    public string Destination = destination;
    public bool Container = container;
    public bool ClearPregnancy = clearPregnancy;
}

[Serializable, NetSerializable]
public sealed partial class BlueMoonClimaxDoAfterEvent : SimpleDoAfterEvent
{
    public InteractionPanelClimaxRequestEvent Request = default!;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelBodyStateEvent(BodyFeature[] features) : EntityEventArgs
{
    public BodyFeature[] Features = features;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelStatusEvent(string message, string[]? repeating = null) : EntityEventArgs
{
    public string Message = message;
    public string[] Repeating = repeating ?? Array.Empty<string>();
}

[Serializable, NetSerializable]
public sealed class InteractionPanelAnimationEvent(NetEntity user, NetEntity target, InteractionPanelAnimationSettings settings) : EntityEventArgs
{
    public NetEntity User = user;
    public NetEntity Target = target;
    public InteractionPanelAnimationSettings Settings = settings;
}
