using System;
using System.Collections.Generic;
using Content.Server.Shuttles.Components;

namespace Content.Server.Shuttles.Systems;

/// <summary>A docking port was removed or moved to another parent.</summary>
public sealed class DockingPortsChangedEvent : EntityEventArgs;

public sealed partial class DockingSystem
{
    private readonly Dictionary<EntityUid, List<Entity<DockingComponent>>> _docksByParent = new();
    private readonly Stack<List<Entity<DockingComponent>>> _dockListPool = new();
    private bool _dockIndexDirty = true;

    private void OnDockIndexAdded(Entity<DockingComponent> ent, ref ComponentAdd args) => _dockIndexDirty = true;
    private void OnDockIndexRemoved(Entity<DockingComponent> ent, ref ComponentRemove args) => DockPortsChanged();
    private void OnDockIndexParentChanged(Entity<DockingComponent> ent, ref EntParentChangedMessage args) => DockPortsChanged();

    private void DockPortsChanged()
    {
        _dockIndexDirty = true;
        RaiseLocalEvent(new DockingPortsChangedEvent());
    }

    /// <summary>
    /// Immediate-child ports, including paused ports, matching GetChildEntities.
    /// Internal view for synchronous traversal; do not retain it across topology changes.
    /// </summary>
    internal IReadOnlyList<Entity<DockingComponent>> GetDockPorts(EntityUid parent)
    {
        if (_dockIndexDirty)
        {
            foreach (var list in _docksByParent.Values)
            {
                list.Clear();
                _dockListPool.Push(list);
            }
            _docksByParent.Clear();

            var query = AllEntityQuery<DockingComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var dock, out var xform))
            {
                if (!_docksByParent.TryGetValue(xform.ParentUid, out var ports))
                {
                    ports = _dockListPool.Count == 0 ? new List<Entity<DockingComponent>>() : _dockListPool.Pop();
                    _docksByParent.Add(xform.ParentUid, ports);
                }
                ports.Add((uid, dock));
            }
            _dockIndexDirty = false;
        }

        return _docksByParent.TryGetValue(parent, out var result) ? result : Array.Empty<Entity<DockingComponent>>();
    }

    public override void Shutdown()
    {
        _docksByParent.Clear();
        _dockListPool.Clear();
        _dockIndexDirty = true;
        base.Shutdown();
    }
}
