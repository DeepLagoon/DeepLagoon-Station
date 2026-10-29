// Adapted from Einstein Engines' trait functions.
using System.Linq;
using Content.Shared.Traits;
using Content.Shared.Traits.Assorted.Components;
using Content.Shared.Damage;
using Content.Shared.Weapons.Melee;
using Content.Shared.Tag;
using Content.Shared.Psionics;
using Content.Server.Abilities.Psionics;
using Content.Server._EinsteinEngines.Language;
using JetBrains.Annotations;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;

namespace Content.Server.Traits;

[UsedImplicitly]
public sealed partial class TraitReplaceComponent : TraitFunction
{
    [DataField, AlwaysPushInheritance]
    public ComponentRegistry Components { get; private set; } = new();

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        foreach (var (_, data) in Components)
        {
            var comp = (Component) serializationManager.CreateCopy(data.Component, notNullableOverride: true);
            comp.Owner = uid;
            entityManager.AddComponent(uid, comp, true);
            if (!comp.GetType().HasCustomAttribute<NetworkedComponentAttribute>())
                continue;

            entityManager.Dirty(uid, comp);
        }
    }
}

[UsedImplicitly]
public sealed partial class TraitAddComponent : TraitFunction
{
    [DataField, AlwaysPushInheritance]
    public ComponentRegistry Components { get; private set; } = new();

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        foreach (var entry in Components.Values)
        {
            if (entityManager.HasComponent(uid, entry.Component.GetType()))
                continue;

            var comp = (Component) serializationManager.CreateCopy(entry.Component, notNullableOverride: true);
            comp.Owner = uid;
            entityManager.AddComponent(uid, comp);

            if (!comp.GetType().HasCustomAttribute<NetworkedComponentAttribute>())
                continue;
            entityManager.Dirty(uid, comp);
        }
    }
}

[UsedImplicitly]
public sealed partial class TraitRemoveComponent : TraitFunction
{
    [DataField, AlwaysPushInheritance]
    public ComponentRegistry Components { get; private set; } = new();

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        foreach (var (name, _) in Components)
            entityManager.RemoveComponentDeferred(uid, factory.GetRegistration(name).Type);
    }
}

[UsedImplicitly]
public sealed partial class TraitAddPsionics : TraitFunction
{
    [DataField, AlwaysPushInheritance]
    public List<ProtoId<PsionicPowerPrototype>> PsionicPowers { get; private set; } = new();

    [DataField, AlwaysPushInheritance]
    public bool PlayFeedback;

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        var prototype = IoCManager.Resolve<IPrototypeManager>();
        var psionic = entityManager.System<PsionicAbilitiesSystem>();

        foreach (var powerProto in PsionicPowers)
            if (prototype.TryIndex(powerProto, out var psionicPower))
                psionic.InitializePsionicPower(uid, psionicPower, PlayFeedback);
    }
}

[UsedImplicitly]
public sealed partial class TraitModifyLanguages : TraitFunction
{
    /// The list of all Spoken Languages that this trait adds.
    [DataField, AlwaysPushInheritance]
    public List<string>? LanguagesSpoken { get; private set; } = default!;

    /// The list of all Understood Languages that this trait adds.
    [DataField, AlwaysPushInheritance]
    public List<string>? LanguagesUnderstood { get; private set; } = default!;

    /// The list of all Spoken Languages that this trait removes.
    [DataField, AlwaysPushInheritance]
    public List<string>? RemoveLanguagesSpoken { get; private set; } = default!;

    /// The list of all Understood Languages that this trait removes.
    [DataField, AlwaysPushInheritance]
    public List<string>? RemoveLanguagesUnderstood { get; private set; } = default!;

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        var language = entityManager.System<LanguageSystem>();

        if (RemoveLanguagesSpoken is not null)
            foreach (var lang in RemoveLanguagesSpoken)
                language.RemoveLanguage(uid, lang, true, false);

        if (RemoveLanguagesUnderstood is not null)
            foreach (var lang in RemoveLanguagesUnderstood)
                language.RemoveLanguage(uid, lang, false, true);

        if (LanguagesSpoken is not null)
            foreach (var lang in LanguagesSpoken)
                language.AddLanguage(uid, lang, true, false);

        if (LanguagesUnderstood is not null)
            foreach (var lang in LanguagesUnderstood)
                language.AddLanguage(uid, lang, false, true);
    }
}

[UsedImplicitly]
public sealed partial class TraitPushDescription : TraitFunction
{
    [DataField, AlwaysPushInheritance]
    public List<DescriptionExtension> DescriptionExtensions { get; private set; } = new();

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        entityManager.EnsureComponent<ExtendDescriptionComponent>(uid, out var descComp);
        foreach (var descExtension in DescriptionExtensions)
            descComp.DescriptionList.Add(descExtension);
    }
}

[UsedImplicitly]
public sealed partial class TraitModifyDensity : TraitFunction
{
    [DataField, AlwaysPushInheritance]
    public float DensityModifier;

    [DataField, AlwaysPushInheritance]
    public bool Multiply = false;

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();
        if (!entityManager.TryGetComponent<FixturesComponent>(uid, out var fixturesComponent)
            || fixturesComponent.Fixtures.Count is 0)
            return;

        var fixture = fixturesComponent.Fixtures.First();
        var newDensity = Multiply ? fixture.Value.Density * DensityModifier : fixture.Value.Density + DensityModifier;
        physicsSystem.SetDensity(uid, fixture.Key, fixture.Value, newDensity);
        // SetDensity handles the Dirty.
    }
}

[UsedImplicitly]
public sealed partial class TraitModifyUnarmed : TraitFunction
{
    // <summary>
    //     The sound played on hitting targets.
    // </summary>
    [DataField, AlwaysPushInheritance]
    public SoundSpecifier? SoundHit;

    // <summary>
    //     The animation to play on hit, for both light and power attacks.
    // </summary>
    [DataField, AlwaysPushInheritance]
    public EntProtoId? Animation;

    // <summary>
    //     Whether to set the power attack animation to be the same as the light attack.
    // </summary>
    [DataField, AlwaysPushInheritance]
    public bool HeavyAnimationFromLight = true;

    // <summary>
    //     The damage values of unarmed damage.
    // </summary>
    [DataField, AlwaysPushInheritance]
    public DamageSpecifier? Damage;

    // <summary>
    //     Additional damage added to the existing damage.
    // </summary>
    [DataField, AlwaysPushInheritance]
    public DamageSpecifier? FlatDamageIncrease;

    /// <summary>
    ///   Turns the left click into a power attack when the light attack misses.
    /// </summary>
    [DataField]
    public bool? HeavyOnLightMiss;

    // <summary>
    //     What to multiply the melee weapon range by.
    // </summary>
    [DataField, AlwaysPushInheritance]
    public float? RangeModifier;

    // <summary>
    //     What to multiply the attack rate by.
    // </summary>
    [DataField, AlwaysPushInheritance]
    public float? AttackRateModifier;

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        if (!entityManager.TryGetComponent<MeleeWeaponComponent>(uid, out var melee))
            return;

        if (SoundHit != null)
            melee.HitSound = SoundHit;

        if (Animation != null)
            melee.Animation = Animation.Value;

        if (HeavyAnimationFromLight)
            melee.WideAnimation = melee.Animation;

        if (Damage != null)
            melee.Damage = Damage;

        if (FlatDamageIncrease != null)
            melee.Damage += FlatDamageIncrease;

        if (HeavyOnLightMiss != null)
            melee.HeavyOnLightMiss = HeavyOnLightMiss.Value;

        if (RangeModifier != null)
            melee.Range *= RangeModifier.Value;

        if (AttackRateModifier != null)
            melee.AttackRate *= AttackRateModifier.Value;

        entityManager.Dirty(uid, melee);
    }
}

[UsedImplicitly]
public sealed partial class TraitAddTag : TraitFunction
{
    [DataField, AlwaysPushInheritance]
    public List<ProtoId<TagPrototype>> Tags { get; private set; } = new();

    public override void OnPlayerSpawn(EntityUid uid,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        var tagSystem = entityManager.System<TagSystem>();
        tagSystem.AddTags(uid, Tags);
    }
}
