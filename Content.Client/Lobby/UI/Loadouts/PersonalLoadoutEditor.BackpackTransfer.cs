using System.Linq;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Clothing;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.Storage;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI.Loadouts;

public sealed partial class PersonalLoadoutEditor
{
    internal HumanoidCharacterProfile TransferBackpack(string sourceJob, Func<HumanoidCharacterProfile, EntityUid> createPreview)
    {
        var profile = _profile!;
        var sourceRoleId = LoadoutSystem.GetJobPrototype(sourceJob);
        if (sourceJob == _job || !profile.Loadouts.TryGetValue(sourceRoleId, out var savedSource) ||
            !_prototypes.TryIndex<JobPrototype>(_job, out var targetJob))
            return profile;

        var dependencies = IoCManager.Instance!;
        var source = savedSource.Clone();
        source.EnsureValid(profile, _session, dependencies);
        BuildBackpackPreview(targetJob, () => createPreview(profile));
        if (_jobRole == null || !_backpackContents.Containers.ContainsKey(""))
            return profile;

        var original = _jobRole;
        var role = original.Clone();
        var packed = _backpackContents.Items.Keys.Where(key => IsPacked(key, original)).ToHashSet();
        var selected = original.SelectedLoadouts.Values.SelectMany(items => items).Select(item => item.Prototype).ToHashSet();
        var catalog = _jobEntries.Where(entry => SharedStationSpawningSystem.IsBackpackLoadout(entry.Item, _prototypes))
            .Select(entry => (Entry: entry, Items: GetStorageItems(entry.Item).ToArray())).ToArray();
        var aliases = new Dictionary<string, string>();
        foreach (var choice in source.SelectedLoadouts.Values.SelectMany(items => items))
        {
            var prototype = _prototypes.Index(choice.Prototype);
            if (!SharedStationSpawningSystem.IsBackpackLoadout(prototype, _prototypes))
                continue;
            var sourceItems = GetStorageItems(prototype).ToArray();
            if (sourceItems.All(item => source.BackpackLayout.GetValueOrDefault(item.Key!).Removed))
                continue;

            // Jobs can list the same backpack item under different loadout IDs and prices.
            var entities = sourceItems.Select(item => item.Prototype).ToArray();
            var matches = catalog.Where(entry => entry.Items.Select(item => item.Prototype).SequenceEqual(entities))
                .OrderByDescending(entry => selected.Contains(entry.Entry.Item.ID))
                .ThenByDescending(entry => entry.Entry.Item.ID == prototype.ID);
            foreach (var (entry, items) in matches)
            {
                if (selected.Contains(entry.Item.ID))
                {
                    MapItems(items);
                    break;
                }
                if (role.SelectedLoadouts[entry.Group.ID].Count >= entry.Group.MaxLimit ||
                    !role.IsValid(profile, _session, entry.Item.ID, dependencies, out _) ||
                    !CanAffordJob(role, entry.Group, entry.Item))
                    continue;

                var candidate = role.Clone();
                candidate.SelectedLoadouts[entry.Group.ID].Add(new Loadout { Prototype = entry.Item.ID, Customization = choice.Customization });
                candidate.EnsureValid(profile, _session, dependencies);
                if (!candidate.SelectedLoadouts[entry.Group.ID].Any(item => item.Prototype.Id == entry.Item.ID) ||
                    role.SelectedLoadouts.Any(group => !group.Value.All(candidate.SelectedLoadouts[group.Key].Contains)))
                    continue;

                // A shared loadout can still be too big for the new backpack.
                var keys = items.Select(item => item.Key!).ToArray();
                Preview(candidate);
                if (keys.Any(key => !IsPacked(key, candidate)) || packed.Any(key => !IsPacked(key, candidate)))
                    continue;
                selected.Add(entry.Item.ID);
                packed.UnionWith(keys);
                MapItems(items);
                role = candidate;
                break;
            }

            void MapItems(StartingGearStorageItem[] items)
            {
                for (var i = 0; i < items.Length; i++)
                    aliases.TryAdd(sourceItems[i].Key!, items[i].Key!);
            }
        }

        if (source.BackpackLayout.Count == 0)
            return role.Equals(original) ? profile : profile.WithLoadout(role);

        if (!_jobRole!.Equals(role))
            Preview(role);
        var targetKit = _backpackDefaults.FirstOrDefault(SharedStationSpawningSystem.IsStartingSurvivalKit).Key;
        var sourceKit = source.SelectedLoadouts.Values.SelectMany(items => items)
            .SelectMany(item => GetStorageItems(_prototypes.Index(item.Prototype)))
            .FirstOrDefault(SharedStationSpawningSystem.IsStartingSurvivalKit).Key ?? SharedStationSpawningSystem.DefaultSurvivalKit;
        if (targetKit != null)
            aliases[sourceKit] = targetKit;
        var targetKeys = _backpackContents.Items.Keys.ToHashSet();
        var targetContainers = _backpackContents.Containers.Keys.ToHashSet();
        var transferred = new Dictionary<string, BackpackItemPlacement>();
        foreach (var (sourceKey, saved) in source.BackpackLayout)
        {
            var key = MapKey(sourceKey);
            var container = saved.Container == null ? null : MapKey(saved.Container);
            if (role.BackpackLayout.ContainsKey(key) || !targetKeys.Contains(key) || key.Contains("/hand/", StringComparison.Ordinal) ||
                container != null && (!targetContainers.Contains(container) || container.Contains("/hand/", StringComparison.Ordinal)) ||
                role.BackpackLayout.Count + transferred.Count >= 256)
                continue;
            transferred.TryAdd(key, saved with { Container = container });
        }

        if (transferred.Count > 0)
        {
            // Restore moves together so two items can trade places.
            var fromSource = role.Clone();
            foreach (var (key, placement) in transferred)
                fromSource.BackpackLayout.Add(key, placement);
            var required = targetKeys.Where(key => IsPacked(key, role) && !transferred.Any(entry => entry.Value.Removed &&
                (key == entry.Key || key.StartsWith(entry.Key + "/contents/", StringComparison.Ordinal)))).ToArray();
            Preview(fromSource);
            if (required.All(key => IsPacked(key, fromSource)) &&
                transferred.All(entry => entry.Value.Removed || IsPacked(entry.Key, fromSource)))
                role = fromSource;
            else
            {
                // A smaller grid can reject an old position. Keep the moves that still fit.
                foreach (var (key, placement) in transferred.OrderByDescending(entry => entry.Value.Removed))
                {
                    var candidate = role.Clone();
                    candidate.BackpackLayout.Add(key, placement);
                    Preview(candidate);
                    if ((placement.Removed || IsPacked(key, candidate)) && required.All(item => IsPacked(item, candidate)))
                        role = candidate;
                }
            }
        }

        return role.Equals(original) ? profile : profile.WithLoadout(role);

        void Preview(RoleLoadout loadout)
        {
            var draft = profile.WithLoadout(loadout);
            Refresh(draft, _job, _session);
            BuildBackpackPreview(targetJob, () => createPreview(draft));
        }

        IEnumerable<StartingGearStorageItem> GetStorageItems(LoadoutPrototype item)
        {
            if (_prototypes.TryIndex(item.StartingGear, out var gear))
                foreach (var content in SharedStationSpawningSystem.GetStartingGearStorageItems(gear).Where(content => content.Key != null))
                    yield return content;
            foreach (var content in SharedStationSpawningSystem.GetStartingGearStorageItems(item).Where(content => content.Key != null))
                yield return content;
        }

        string MapKey(string key)
        {
            var separator = key.IndexOf("/contents/", StringComparison.Ordinal);
            var root = separator < 0 ? key : key[..separator];
            return aliases.TryGetValue(root, out var mapped) ? mapped + key[root.Length..] : key;
        }
    }

    private bool IsPacked(string key, RoleLoadout role)
    {
        while (_backpackContents.Items.TryGetValue(key, out var item))
        {
            var container = TargetContainer(key, item.Origin, role.BackpackLayout);
            if (!_backpackContents.Containers.TryGetValue(container, out var uid) ||
                !_entities.TryGetComponent<StorageComponent>(uid, out var storage) || !storage.StoredItems.ContainsKey(item.Entity))
                return false;
            if (container == "")
                return true;
            key = container;
        }
        return false;
    }
}
