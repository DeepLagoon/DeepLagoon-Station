using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.Player;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    private readonly Dictionary<NetUserId, TimeSpan> _lastShopPreview = new();

    private void PreviewShop(ICommonSession session, ApartmentShopAction ev)
    {
        if (!CanManage(session) || ev.Template.Length is < 1 or > 128 ||
            !_prototypes.TryIndex<ApartmentTemplatePrototype>(ev.Template, out var template) || !CanSelectTemplate(session.UserId, template)) return;
        if (_lastShopPreview.TryGetValue(session.UserId, out var last) && _timing.RealTime - last < TimeSpan.FromMilliseconds(500))
        {
            RaiseNetworkEvent(new ApartmentShopPreview(ev.Token, template.ID, null, "Слишком частые запросы. Повторите предпросмотр."), session);
            return;
        }
        _lastShopPreview[session.UserId] = _timing.RealTime;
        try
        {
            var scene = BuildShopPreview(session.UserId, template);
            RaiseNetworkEvent(new ApartmentShopPreview(ev.Token, template.ID, scene), session);
        }
        catch (Exception e)
        {
            Log.Warning($"Apartment preview failed for {template.ID}: {e.GetType().Name}");
            RaiseNetworkEvent(new ApartmentShopPreview(ev.Token, template.ID, null, "Не удалось загрузить предпросмотр. Повторите запрос."), session);
        }
    }

    private ApartmentRoomScene BuildShopPreview(NetUserId owner, ApartmentTemplatePrototype template)
    {
        EnsureMaps();
        // A fresh template, never the owner's saved room. No grants, selection or payment.
        var preview = CreateFresh(owner, template.ID, true);
        try
        {
            var scene = CaptureRoom(preview);
            foreach (var uid in preview.Furniture.Values)
            {
                if (!Exists(uid) || MetaData(uid).EntityPrototype is not { } prototype) continue;
                var transform = Transform(uid);
                scene.Objects.Add(new GridEditorItem { Id = "preview:" + GetNetEntity(uid), Kind = "entity", Prototype = prototype.ID,
                    X = transform.LocalPosition.X, Y = transform.LocalPosition.Y, Rotation = (float) transform.LocalRotation.Degrees });
            }
            return scene;
        }
        finally { Del(preview.Grid); }
    }
}
