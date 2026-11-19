using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Inventory;
using Content.Shared.Silicons.Borgs;
using Content.Shared.Verbs;
using Robust.Shared.GameStates;
using Robust.Shared.Utility;
using Content.Shared.Explosion;

namespace Content.Shared.Armor;

/// <summary>
///     This handles logic relating to <see cref="ArmorComponent" />
/// </summary>
public abstract partial class SharedArmorSystem : EntitySystem
{
    [Dependency] private ExamineSystemShared _examine = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ArmorComponent, InventoryRelayedEvent<CoefficientQueryEvent>>(OnCoefficientQuery);
        SubscribeLocalEvent<ArmorComponent, InventoryRelayedEvent<DamageModifyEvent>>(OnDamageModify);
        SubscribeLocalEvent<ArmorComponent, BorgModuleRelayedEvent<DamageModifyEvent>>(OnBorgDamageModify);
        SubscribeLocalEvent<ArmorComponent, GetVerbsEvent<ExamineVerb>>(OnArmorVerbExamine);
        SubscribeLocalEvent<ArmorComponent, InventoryRelayedEvent<GetExplosionResistanceEvent>>(OnCoveredExplosionResistance);
    }

    /// <summary>
    /// Get the total Damage reduction value of all equipment caught by the relay.
    /// </summary>
    /// <param name="ent">The item that's being relayed to</param>
    /// <param name="args">The event, contains the running count of armor percentage as a coefficient</param>
    private void OnCoefficientQuery(Entity<ArmorComponent> ent, ref InventoryRelayedEvent<CoefficientQueryEvent> args)
    {
        var modifiers = ent.Comp.Modifiers;
        if (ent.Comp.Coverage != null)
        {
            if (args.Args.CoveredArmorApplied) return;
            args.Args.CoveredArmorApplied = true;
            modifiers = GetCoveredModifiers(ent, null, out _);
        }
        foreach (var armorCoefficient in modifiers.Coefficients)
        {
            args.Args.DamageModifiers.Coefficients[armorCoefficient.Key] = args.Args.DamageModifiers.Coefficients.TryGetValue(armorCoefficient.Key, out var coefficient) ? coefficient * armorCoefficient.Value : armorCoefficient.Value;
        }
    }

    private void OnDamageModify(EntityUid uid, ArmorComponent component, InventoryRelayedEvent<DamageModifyEvent> args)
    {
        DamageArmorOverrideComponent? armorOverride = null;
        if (args.Args.Tool is {} tool && TryComp<DamageArmorOverrideComponent>(tool, out armorOverride) && armorOverride.IgnoreArmor)
            return;
        var modifiers = component.Modifiers;
        if (component.Coverage != null)
        {
            if (args.Args.CoveredArmorApplied) return;
            args.Args.CoveredArmorApplied = true;
            modifiers = GetCoveredModifiers(uid, args.Args.TargetPart, out _);
        }
        if (armorOverride?.ArmorType is {} armorType)
        {
            var alternate = new DamageModifierSet();
            foreach (var type in args.Args.Damage.DamageDict.Keys)
            {
                alternate.Coefficients[type] = modifiers.Coefficients.GetValueOrDefault(armorType, 1);
                alternate.FlatReduction[type] = modifiers.FlatReduction.GetValueOrDefault(armorType);
            }
            modifiers = alternate;
        }
        args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage,
            component.Coverage != null || armorOverride?.SourceArmorPenetration == true
                ? PenetrateSourceArmor(modifiers, args.Args.ArmorPenetration)
                : DamageSpecifier.PenetrateArmor(modifiers, args.Args.ArmorPenetration));
    }

    private void OnBorgDamageModify(EntityUid uid, ArmorComponent component,
        ref BorgModuleRelayedEvent<DamageModifyEvent> args)
    {
        args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, DamageSpecifier.PenetrateArmor(component.Modifiers, args.Args.ArmorPenetration)); // Goob edit
    }

    private void OnArmorVerbExamine(EntityUid uid, ArmorComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        var examineMarkup = GetArmorExamine(component.Modifiers);

        var ev = new ArmorExamineEvent(examineMarkup);
        RaiseLocalEvent(uid, ref ev);

        _examine.AddDetailedExamineVerb(args, component, examineMarkup,
            Loc.GetString("armor-examinable-verb-text"), "/Textures/Interface/VerbIcons/dot.svg.192dpi.png",
            Loc.GetString("armor-examinable-verb-message"));
    }

    private FormattedMessage GetArmorExamine(DamageModifierSet armorModifiers)
    {
        var msg = new FormattedMessage();
        msg.AddMarkupOrThrow(Loc.GetString("armor-examine"));

        foreach (var coefficientArmor in armorModifiers.Coefficients)
        {
            msg.PushNewline();

            var armorType = Loc.GetString("armor-damage-type-" + coefficientArmor.Key.Id.ToLowerInvariant());
            msg.AddMarkupOrThrow(Loc.GetString("armor-coefficient-value",
                ("type", armorType),
                ("value", MathF.Round((1f - coefficientArmor.Value) * 100, 1))
            ));
        }

        foreach (var flatArmor in armorModifiers.FlatReduction)
        {
            msg.PushNewline();

            var armorType = Loc.GetString("armor-damage-type-" + flatArmor.Key.Id.ToLowerInvariant());
            msg.AddMarkupOrThrow(Loc.GetString("armor-reduction-value",
                ("type", armorType),
                ("value", flatArmor.Value)
            ));
        }

        return msg;
    }
}
