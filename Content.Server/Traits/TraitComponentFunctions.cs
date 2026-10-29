using Content.Shared.Inventory;
using System.Linq;
using System.Reflection;
using Robust.Shared.GameStates;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Traits;
using Content.Shared.Traits.Assorted.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.Server.Traits;

[DataDefinition]
public sealed partial class TraitComponentPatch
{
    [DataField(required: true)] public ComponentRegistry Components = new();
    [DataField(required: true)] public HashSet<string> Fields = new();
}

/// <summary>Updates explicitly supplied data fields, including zero, false and null, without replacing live component state.</summary>
public sealed partial class TraitModifyComponent : TraitFunction
{
    [DataField] public List<TraitComponentPatch> Components = new();

    public override void OnPlayerSpawn(EntityUid uid, IComponentFactory factory,
        IEntityManager entityManager, ISerializationManager serializationManager)
    {
        foreach (var patch in Components)
        foreach (var (name, entry) in patch.Components)
        {
            var registration = factory.GetRegistration(name);
            var copy = (Component) serializationManager.CreateCopy(entry.Component, notNullableOverride: true);
            if (!entityManager.TryGetComponent(uid, registration.Type, out var component))
            {
                entityManager.AddComponent(uid, copy);
                continue;
            }
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var key in patch.Fields)
            {
                var member = registration.Type.GetMembers(flags).FirstOrDefault(m =>
                    m.GetCustomAttribute<DataFieldAttribute>() is { } field
                    && (field.Tag ?? char.ToLowerInvariant(m.Name[0]) + m.Name[1..]) == key);
                switch (member)
                {
                    case FieldInfo field:
                        field.SetValue(component, field.GetValue(copy));
                        break;
                    case PropertyInfo property:
                        property.SetValue(component, property.GetValue(copy));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown trait field {name}.{key}");
                }
            }
            if (component is InventoryComponent inventory && patch.Fields.Contains("templateId"))
                entityManager.System<InventorySystem>().RefreshTraitTemplate(uid, inventory);
            if (component is Content.Shared.Speech.Components.VocalComponent vocal && patch.Fields.Contains("sounds"))
                entityManager.System<Content.Server.Speech.EntitySystems.VocalSystem>().RefreshTraitSounds(uid, vocal);
            if (registration.Type.GetCustomAttribute<NetworkedComponentAttribute>() != null)
                entityManager.Dirty(uid, component);
        }
    }
}

public sealed partial class TraitCyberneticLimbReplacement : TraitFunction
{
    [DataField] public BodyPartType RemoveBodyPart = BodyPartType.Arm;
    [DataField] public BodyPartSymmetry PartSymmetry = BodyPartSymmetry.Left;
    [DataField(required: true)] public EntProtoId ProtoId;
    [DataField(required: true)] public string SlotId = "";

    public override void OnPlayerSpawn(EntityUid uid, IComponentFactory factory,
        IEntityManager entityManager, ISerializationManager serializationManager)
    {
        var bodySystem = entityManager.System<SharedBodySystem>();
        if (!entityManager.TryGetComponent(uid, out BodyComponent? body)) return;
        var root = bodySystem.GetRootPartOrNull(uid, body);
        if (root == null) return;
        foreach (var part in bodySystem.GetBodyChildrenOfType(uid, RemoveBodyPart, body).ToArray())
        {
            if (part.Component.Symmetry != PartSymmetry) continue;
            var limb = entityManager.SpawnEntity(ProtoId, entityManager.GetComponent<TransformComponent>(uid).Coordinates);
            entityManager.System<SharedTransformSystem>().AttachToGridOrMap(part.Id);
            if (entityManager.TryGetComponent(limb, out BodyPartComponent? replacement)
                && bodySystem.AttachPart(root.Value.Entity, SlotId, limb, root.Value.BodyPart, replacement))
                entityManager.QueueDeleteEntity(part.Id);
            else
            {
                entityManager.QueueDeleteEntity(limb);
                bodySystem.AttachPart(root.Value.Entity, SlotId, part.Id, root.Value.BodyPart, part.Component);
            }
        }
    }
}

public sealed partial class TraitAddArmor : TraitFunction
{
    [DataField] public List<ProtoId<DamageModifierSetPrototype>> DamageModifierSets = new();

    public override void OnPlayerSpawn(EntityUid uid, IComponentFactory factory,
        IEntityManager entityManager, ISerializationManager serializationManager)
    {
        entityManager.EnsureComponent<TraitArmorComponent>(uid, out var armor);
        armor.ModifierSets.UnionWith(DamageModifierSets);
        entityManager.Dirty(uid, armor);
    }
}
