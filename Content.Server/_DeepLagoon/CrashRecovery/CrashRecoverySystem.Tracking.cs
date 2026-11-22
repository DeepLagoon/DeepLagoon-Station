namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    private readonly Dictionary<EntityUid, int> _trackedIndices = new();

    private void Track(EntityUid uid)
    {
        if (_serializer != null && _assets.TryGetValue(uid, out var asset) && asset.YamlId > 0)
            _serializer.YamlUidMap[uid] = asset.YamlId;
        if (!_trackedIndices.TryAdd(uid, _tracked.Count)) return;
        _tracked.Add(uid);
    }

    /// <summary>
    /// Retire only the live ECS bookkeeping. The pending/durable tombstone and purchase receipt
    /// must remain authoritative even when an earlier snapshot is still being written.
    /// </summary>
    private void Retire(EntityUid uid)
    {
        if (_assets.Remove(uid, out var asset))
        {
            _entitiesByIdentity.Remove(asset.Identity);
            _serializer?.YamlUidMap.Remove(uid);
        }
        if (_trackedIndices.Remove(uid, out var index))
        {
            var last = _tracked[^1];
            _tracked[index] = last;
            _trackedIndices[last] = index;
            _tracked.RemoveAt(_tracked.Count - 1);
            if (last == uid) _trackedIndices.Remove(uid);
            if (_pollCursor > index) _pollCursor--;
        }
        _dirty.Remove(uid); _discover.Remove(uid);
        _discoveredEntities.Remove(uid); _childrenDiscovered.Remove(uid);
        _capturedTicks.Remove(uid); _captureAfter.Remove(uid); _failedUntil.Remove(uid);
    }
}
