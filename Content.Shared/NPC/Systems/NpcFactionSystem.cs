using Content.Shared.Mind.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Robust.Shared.Prototypes;
using System.Collections.Frozen;
using System.Linq;

namespace Content.Shared.NPC.Systems;

/// <summary>
///     Outlines faction relationships with each other.
/// </summary>
public sealed partial class NpcFactionSystem : EntitySystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private SharedTransformSystem _xform = default!;

    /// <summary>
    /// To avoid prototype mutability we store an intermediary data class that gets used instead.
    /// </summary>
    private FrozenDictionary<string, FactionData> _factions = FrozenDictionary<string, FactionData>.Empty;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcFactionMemberComponent, ComponentStartup>(OnFactionStartup);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnProtoReload);

        InitializeException();
        RefreshFactions();
    }

    private void OnProtoReload(PrototypesReloadedEventArgs obj)
    {
        if (obj.WasModified<NpcFactionPrototype>())
            RefreshFactions();
    }

    private void OnFactionStartup(Entity<NpcFactionMemberComponent> ent, ref ComponentStartup args)
    {
        RefreshFactions(ent);
    }

    /// <summary>
    /// Refreshes the cached factions for this component.
    /// </summary>
    private void RefreshFactions(Entity<NpcFactionMemberComponent> ent)
    {
        ent.Comp.FriendlyFactions.Clear();
        ent.Comp.HostileFactions.Clear();

        foreach (var faction in ent.Comp.Factions)
        {
            // YAML Linter already yells about this, don't need to log an error here
            if (!_factions.TryGetValue(faction, out var factionData))
                continue;

            ent.Comp.FriendlyFactions.UnionWith(factionData.Friendly);
            ent.Comp.HostileFactions.UnionWith(factionData.Hostile);
        }
        // Add additional factions if it is written in prototype
        if (ent.Comp.AddFriendlyFactions != null)
        {
            ent.Comp.FriendlyFactions.UnionWith(ent.Comp.AddFriendlyFactions);
        }
        if (ent.Comp.AddHostileFactions != null)
        {
            ent.Comp.HostileFactions.UnionWith(ent.Comp.AddHostileFactions);
        }
    }

    /// <summary>
    /// Returns whether an entity is a member of a faction.
    /// </summary>
    public bool IsMember(Entity<NpcFactionMemberComponent?> ent, string faction)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return false;

        return ent.Comp.Factions.Contains(faction);
    }

    /// <summary>
    /// Returns whether an entity is a member of any listed faction.
    /// If the list is empty this returns false.
    /// </summary>
    public bool IsMemberOfAny(Entity<NpcFactionMemberComponent?> ent, IEnumerable<ProtoId<NpcFactionPrototype>> factions)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return false;

        foreach (var faction in factions)
        {
            if (ent.Comp.Factions.Contains(faction))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Adds this entity to the particular faction.
    /// </summary>
    public void AddFaction(Entity<NpcFactionMemberComponent?> ent, string faction, bool dirty = true)
    {
        if (!_proto.HasIndex<NpcFactionPrototype>(faction))
        {
            Log.Error($"Unable to find faction {faction}");
            return;
        }

        ent.Comp ??= EnsureComp<NpcFactionMemberComponent>(ent);
        if (!ent.Comp.Factions.Add(faction))
            return;

        if (dirty)
            RefreshFactions((ent, ent.Comp));
    }

    /// <summary>
    /// Adds this entity to the particular faction.
    /// </summary>
    public void AddFactions(Entity<NpcFactionMemberComponent?> ent, HashSet<ProtoId<NpcFactionPrototype>> factions, bool dirty = true)
    {
        ent.Comp ??= EnsureComp<NpcFactionMemberComponent>(ent);

        foreach (var faction in factions)
        {
            if (!_proto.HasIndex(faction))
            {
                Log.Error($"Unable to find faction {faction}");
                continue;
            }

            ent.Comp.Factions.Add(faction);
        }

        if (dirty)
            RefreshFactions((ent, ent.Comp));
    }

    /// <summary>
    /// Removes this entity from the particular faction.
    /// </summary>
    public void RemoveFaction(Entity<NpcFactionMemberComponent?> ent, string faction, bool dirty = true)
    {
        if (!_proto.HasIndex<NpcFactionPrototype>(faction))
        {
            Log.Error($"Unable to find faction {faction}");
            return;
        }

        if (!Resolve(ent, ref ent.Comp, false))
            return;

        if (!ent.Comp.Factions.Remove(faction))
            return;

        if (dirty)
            RefreshFactions((ent, ent.Comp));
    }

    /// <summary>
    /// Remove this entity from all factions.
    /// </summary>
    public void ClearFactions(Entity<NpcFactionMemberComponent?> ent, bool dirty = true)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        ent.Comp.Factions.Clear();

        if (dirty)
            RefreshFactions((ent, ent.Comp));
    }

    public IEnumerable<EntityUid> GetNearbyHostiles(Entity<NpcFactionMemberComponent?, FactionExceptionComponent?> ent, float range)
    {
        if (!Resolve(ent, ref ent.Comp1, false))
            return Array.Empty<EntityUid>();

        var hostiles = GetNearbyFactions(ent, range, ent.Comp1.HostileFactions, includeForcedHostiles: true)
            .Union(GetNearbyPlayerHostiles((ent, ent.Comp1), range))
            // ignore mobs that have both hostile faction and the same faction,
            // otherwise having multiple factions is strictly negative
            .Where(target => !IsEntityFriendly((ent, ent.Comp1), target));
        if (!Resolve(ent, ref ent.Comp2, false))
            return hostiles;

        // ignore anything from enemy faction that we are explicitly friendly towards
        var faction = (ent.Owner, ent.Comp2);
        return hostiles
            .Union(GetHostiles(faction))
            .Where(target => !IsIgnored(faction, target));
    }

    public IEnumerable<EntityUid> GetNearbyFriendlies(Entity<NpcFactionMemberComponent?> ent, float range)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return Array.Empty<EntityUid>();

        return GetNearbyFactions(ent, range, ent.Comp.FriendlyFactions)
            .Where(target => !IsForcedHostile(ent, target));
    }

    private IEnumerable<EntityUid> GetNearbyFactions(EntityUid entity, float range, HashSet<ProtoId<NpcFactionPrototype>> factions,
        bool includeForcedHostiles = false)
    {
        var xform = Transform(entity);
        foreach (var ent in _lookup.GetEntitiesInRange<NpcFactionMemberComponent>(_xform.GetMapCoordinates((entity, xform)), range))
        {
            if (ent.Owner == entity)
                continue;

            if (!factions.Overlaps(ent.Comp.Factions) && !(includeForcedHostiles && IsForcedHostile(entity, ent.Owner)))
                continue;

            yield return ent.Owner;
        }
    }

    /// <remarks>
    /// 1-way; forced hostility takes priority over factions. Ignores personal faction exceptions.
    /// </remarks>
    public bool IsEntityFriendly(Entity<NpcFactionMemberComponent?> ent, Entity<NpcFactionMemberComponent?> other)
    {
        if (IsForcedHostile(ent, other))
            return false;

        if (!Resolve(ent, ref ent.Comp, false) || !Resolve(other, ref other.Comp, false))
            return false;

        return ent.Comp.Factions.Overlaps(other.Comp.Factions) || ent.Comp.FriendlyFactions.Overlaps(other.Comp.Factions);
    }

    private bool IsForcedHostile(EntityUid entity, EntityUid target)
    {
        if (entity == target)
            return false;

        if (TryComp<MindContainerComponent>(target, out var targetMind) && targetMind.HasMind &&
            IsHostileToPlayerMinds(entity))
            return !IsPlayerMindAlly(entity, target);

        return TryComp<NpcForcedHostilityComponent>(entity, out var hostility) &&
               (!TryComp<NpcForcedHostilityComponent>(target, out var other) || hostility.Group != other.Group ||
                TryComp<MindContainerComponent>(target, out var mind) && mind.HasMind);
    }

    private bool IsHostileToPlayerMinds(Entity<NpcFactionMemberComponent?> ent)
    {
        // Player-controlled faction members retain their ordinary relationships.
        if (TryComp<MindContainerComponent>(ent, out var mind) && mind.HasMind ||
            !Resolve(ent, ref ent.Comp, false))
            return false;

        foreach (var faction in ent.Comp.Factions)
        {
            if (_factions.TryGetValue(faction, out var data) && data.HostileToPlayerMinds)
                return true;
        }

        return false;
    }

    private bool IsPlayerMindAlly(Entity<NpcFactionMemberComponent?> ent, Entity<NpcFactionMemberComponent?> target)
    {
        if (!Resolve(ent, ref ent.Comp, false) || !Resolve(target, ref target.Comp, false))
            return false;

        foreach (var faction in ent.Comp.Factions)
        {
            if (_factions.TryGetValue(faction, out var data) && data.HostileToPlayerMinds &&
                data.PlayerMindFriendlyFactions.Overlaps(target.Comp.Factions))
                return true;
        }

        return false;
    }

    private IEnumerable<EntityUid> GetNearbyPlayerHostiles(Entity<NpcFactionMemberComponent?> ent, float range)
    {
        if (!IsHostileToPlayerMinds(ent))
            yield break;

        // Include player bodies without NpcFactionMember as well.
        var coordinates = _xform.GetMapCoordinates(ent.Owner);
        foreach (var target in _lookup.GetEntitiesInRange<MindContainerComponent>(coordinates, range))
        {
            if (target.Owner != ent.Owner && target.Comp.HasMind)
                yield return target.Owner;
        }
    }

    public bool IsFactionFriendly(string target, string with)
    {
        return _factions[target].Friendly.Contains(with) && _factions[with].Friendly.Contains(target);
    }

    public bool IsFactionFriendly(string target, Entity<NpcFactionMemberComponent?> with)
    {
        if (!Resolve(with, ref with.Comp, false))
            return false;

        return with.Comp.Factions.All(x => IsFactionFriendly(target, x)) ||
               with.Comp.FriendlyFactions.Contains(target);
    }

    public bool IsFactionHostile(string target, string with)
    {
        return _factions[target].Hostile.Contains(with) && _factions[with].Hostile.Contains(target);
    }

    public bool IsFactionHostile(string target, Entity<NpcFactionMemberComponent?> with)
    {
        if (!Resolve(with, ref with.Comp, false))
            return false;

        return with.Comp.Factions.All(x => IsFactionHostile(target, x)) ||
               with.Comp.HostileFactions.Contains(target);
    }

    public bool IsFactionNeutral(string target, string with)
    {
        return !IsFactionFriendly(target, with) && !IsFactionHostile(target, with);
    }

    /// <summary>
    /// Makes the source faction friendly to the target faction, 1-way.
    /// </summary>
    public void MakeFriendly(string source, string target)
    {
        if (!_factions.TryGetValue(source, out var sourceFaction))
        {
            Log.Error($"Unable to find faction {source}");
            return;
        }

        if (!_factions.ContainsKey(target))
        {
            Log.Error($"Unable to find faction {target}");
            return;
        }

        sourceFaction.Friendly.Add(target);
        sourceFaction.Hostile.Remove(target);
        RefreshFactions();
    }

    /// <summary>
    /// Makes the source faction hostile to the target faction, 1-way.
    /// </summary>
    public void MakeHostile(string source, string target)
    {
        if (!_factions.TryGetValue(source, out var sourceFaction))
        {
            Log.Error($"Unable to find faction {source}");
            return;
        }

        if (!_factions.ContainsKey(target))
        {
            Log.Error($"Unable to find faction {target}");
            return;
        }

        sourceFaction.Friendly.Remove(target);
        sourceFaction.Hostile.Add(target);
        RefreshFactions();
    }

    /// <summary>
    /// Mono edit - Assigns all factions to hostile except for "Friendly" and "Neutral" ones if "DefaultHostile" is true
    /// </summary>
    private void RefreshFactions()
    {
        var factionPrototypes = _proto.EnumeratePrototypes<NpcFactionPrototype>();

        _factions = factionPrototypes.ToFrozenDictionary(
            faction => faction.ID,
            faction =>  new FactionData
            {
                Friendly = faction.Friendly.ToHashSet(),
                Neutral = faction.Neutral.ToHashSet(),
                Hostile = GetHostileFactions(faction, faction.DefaultHostile, factionPrototypes),
                HostileToPlayerMinds = faction.HostileToPlayerMinds,
                PlayerMindFriendlyFactions = faction.PlayerMindFriendlyFactions.ToHashSet()
            });

        var query = AllEntityQuery<NpcFactionMemberComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            comp.FriendlyFactions.Clear();
            comp.HostileFactions.Clear();
            RefreshFactions((uid, comp));
        }
    }

    /// <summary>
    /// Mono edit - Gets hostile actions either from prototype or sets all factions to hostile except for friendly and neutral ones
    /// </summary>
    private HashSet<ProtoId<NpcFactionPrototype>> GetHostileFactions(NpcFactionPrototype iteratedFaction,
        bool defaultHostile,
        IEnumerable<NpcFactionPrototype> factionPrototypes)
    {
        HashSet<ProtoId<NpcFactionPrototype>> hostile = new();

        if (!defaultHostile)
        {
           return GetHostileFactions(iteratedFaction);
        }

        foreach (var faction in factionPrototypes)
        {
            if (iteratedFaction.Neutral.Contains(faction) ||
                iteratedFaction.Friendly.Contains(faction) ||
                !faction.DefaultHostileIncluded || // Mono
                faction.ID == iteratedFaction.ID)
                continue;

            hostile.Add(faction);
        }
        return hostile;
    }
    /// <summary>
    /// Mono edit - Returns hostile faction from prototype
    /// </summary>
    private HashSet<ProtoId<NpcFactionPrototype>> GetHostileFactions(NpcFactionPrototype faction)
    {
        return faction.Hostile.ToHashSet();
    }
}
