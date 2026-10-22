using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.InteractionPanel;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Ghost.Roles;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Map;

namespace Content.Client.UserInterface.Systems.Ghost.Controls.Roles;

/// <summary>
/// Native, shader-aware sprites over the browser's visible thumbnail slots. No flattened icon layers,
/// GPU readbacks or per-frame image transfers. Positions and dimensions use the full browser viewport.
/// </summary>
public sealed class GhostRoleTguiHost : Container
{
    private readonly TguiPanel _panel;
    private readonly IEntityManager _entities = IoCManager.Resolve<IEntityManager>();
    private readonly List<(InteractionPanelSpriteView View, uint Id, UIBox2 Box)> _slots = new();
    private readonly Dictionary<uint, EntityUid> _dummies = new();
    private Dictionary<uint, GhostRoleInfo> _roles = new();
    private uint[] _subscribed = [];
    private Vector2 _viewportSize;
    public event Action<uint[]>? SubscriptionChanged;

    public GhostRoleTguiHost(TguiPanel panel)
    {
        _panel = panel;
        HorizontalExpand = VerticalExpand = true;
        RectClipContent = true;
        AddChild(panel);
    }

    public void SetRoles(GhostRoleInfo[] roles)
    {
        var next = roles.ToDictionary(role => role.Identifier);
        var changed = next.Values.Where(role => !_roles.TryGetValue(role.Identifier, out var previous) ||
            previous.PreviewEntity != role.PreviewEntity || !Nullable.Equals(previous.PreviewPrototype, role.PreviewPrototype))
            .Select(role => role.Identifier).ToHashSet();
        foreach (var stale in _dummies.Keys.Where(id => !next.ContainsKey(id) || changed.Contains(id)).ToArray()) DeleteDummy(stale);
        _roles = next;
        // Drop thumbnails immediately when their role disappears, before React processes the new state.
        for (var i = _slots.Count - 1; i >= 0; i--)
            if (!_roles.ContainsKey(_slots[i].Id)) RemoveSlot(i);
        foreach (var slot in _slots.Where(slot => changed.Contains(slot.Id))) SetPreviewEntity(slot.View, slot.Id);
        UpdateSubscription();
    }

    public void SetLayout(string layout, float width, float height)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width is <= 0 or > 16384 || height is <= 0 or > 16384)
            return;
        var entries = layout.Length == 0 ? [] : layout.Split(';');
        if (entries.Length > GhostRolePreviewSubscription.MaxRoles || layout.Length > 6000) return;
        var parsed = new List<(uint Id, UIBox2 Box)>();
        foreach (var entry in entries)
        {
            var fields = entry.Split(',');
            if (fields.Length != 5 || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !_roles.ContainsKey(id)) return;
            var values = new float[4];
            for (var i = 0; i < 4; i++)
                if (!float.TryParse(fields[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i])) return;
            var (x, y, w, h) = (values[0], values[1], values[2], values[3]);
            if (x < 0 || y < 0 || w is < 8 or > 192 || h is < 8 or > 192 || x + w > width + 1 || y + h > height + 1) return;
            parsed.Add((id, UIBox2.FromDimensions(new Vector2(x, y), new Vector2(w, h))));
        }
        _viewportSize = new Vector2(width, height);
        for (var i = _slots.Count - 1; i >= parsed.Count; i--) RemoveSlot(i);
        for (var i = 0; i < parsed.Count; i++)
        {
            var (id, box) = parsed[i];
            var view = i < _slots.Count ? _slots[i].View : new InteractionPanelSpriteView
            {
                Stretch = SpriteView.StretchMode.Fill,
                OverrideDirection = Direction.South,
                MouseFilter = MouseFilterMode.Ignore,
            };
            if (i == _slots.Count) { AddChild(view); _slots.Add((view, id, box)); }
            else _slots[i] = (view, id, box);
            SetPreviewEntity(view, id);
        }
        foreach (var stale in _dummies.Keys.Where(id => parsed.All(slot => slot.Id != id)).ToArray()) DeleteDummy(stale);
        UpdateSubscription();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void SetPreviewEntity(InteractionPanelSpriteView view, uint id)
    {
        var role = _roles[id];
        if (role.PreviewEntity is { } entity) view.SetEntity(entity);
        else if (role.PreviewPrototype is { } prototype)
        {
            if (!_dummies.TryGetValue(id, out var dummy))
            {
                // Same nullspace preview path as special lobby job entities. The UI explicitly
                // identifies this as a spawn prototype; randomized server equipment does not exist yet.
                dummy = _entities.SpawnEntity(prototype, MapCoordinates.Nullspace);
                _dummies.Add(id, dummy);
            }
            view.SetEntity(dummy);
        }
        else view.SetEntity((EntityUid?) null);
    }

    private void UpdateSubscription()
    {
        var wanted = _slots.Select(slot => slot.Id).Distinct().Where(id => _roles.TryGetValue(id, out var role) && role.PreviewEntity != null).Order().ToArray();
        if (_subscribed.SequenceEqual(wanted)) return;
        _subscribed = wanted;
        SubscriptionChanged?.Invoke(wanted);
    }

    private void RemoveSlot(int index)
    {
        var view = _slots[index].View;
        _slots.RemoveAt(index);
        view.Orphan();
#pragma warning disable CS0618
        view.Dispose();
#pragma warning restore CS0618
    }

    private void DeleteDummy(uint id)
    {
        if (_dummies.Remove(id, out var dummy) && !_entities.Deleted(dummy)) _entities.DeleteEntity(dummy);
    }

    public void ClearPreviews()
    {
        for (var i = _slots.Count - 1; i >= 0; i--) RemoveSlot(i);
        foreach (var id in _dummies.Keys.ToArray()) DeleteDummy(id);
        UpdateSubscription();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _panel.Measure(availableSize);
        foreach (var slot in _slots) slot.View.Measure(slot.Box.Size);
        return Vector2.Zero;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        _panel.Arrange(UIBox2.FromDimensions(Vector2.Zero, finalSize));
        if (_viewportSize.X <= 0 || _viewportSize.Y <= 0) return finalSize;
        var scale = finalSize / _viewportSize;
        foreach (var slot in _slots)
            slot.View.Arrange(UIBox2.FromDimensions(slot.Box.TopLeft * scale, slot.Box.Size * scale));
        return finalSize;
    }

    protected override void ExitedTree()
    {
        ClearPreviews();
        base.ExitedTree();
    }

    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearPreviews();
        base.Dispose(disposing);
    }
}
