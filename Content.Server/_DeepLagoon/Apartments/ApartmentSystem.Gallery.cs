using System.Linq;
using System.Text.Json;
using Content.Server._DeepLagoon.Art;
using Content.Shared._DeepLagoon.Art;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    private readonly Dictionary<NetUserId, TimeSpan> _lastGalleryRequest = new();

    private void Gallery(ICommonSession session, ApartmentShopAction ev)
    {
        if (!CanManage(session) || ev.Page < 0) return;
        if (_lastGalleryRequest.TryGetValue(session.UserId, out var last) && _timing.RealTime - last < TimeSpan.FromMilliseconds(500))
        {
            RaiseNetworkEvent(new ApartmentGalleryState(ev.Token, [], ev.Page, 0, "Подождите немного и обновите галерею."), session);
            return;
        }
        _lastGalleryRequest[session.UserId] = _timing.RealTime;
        try
        {
            var home = InLobby(session) ? PrepareElevatorApartment(session.UserId) : _visits[session.UserId].Apartment;
            if (home.Owner != session.UserId) return;
            var paintings = GalleryContents(home);
            var page = Math.Min(ev.Page, Math.Max(0, (paintings.Count - 1) / ApartmentGalleryState.PageSize));
            RaiseNetworkEvent(new ApartmentGalleryState(ev.Token,
                paintings.Skip(page * ApartmentGalleryState.PageSize).Take(ApartmentGalleryState.PageSize).ToList(), page, paintings.Count), session);
        }
        catch (Exception e)
        {
            Log.Warning($"Apartment gallery failed for {session.UserId}: {e.GetType().Name}");
            RaiseNetworkEvent(new ApartmentGalleryState(ev.Token, [], ev.Page, 0, "Не удалось загрузить картины квартиры. Повторите запрос."), session);
        }
    }

    private List<ApartmentPainting> GalleryContents(Instance home)
    {
        var paintings = new List<ApartmentPainting>();
        foreach (var uid in ApartmentEntities(home.Grid))
        {
            if (!TryComp<ArtCanvasComponent>(uid, out var canvas)) continue;
            var preview = canvas.DraftDocument.Length > 0 ? canvas.DraftPreview : canvas.SavedPreview;
            if (preview.Length == 0) continue;
            var title = canvas.Title;
            // Only the server-validated document is read, never client-supplied gallery JSON.
            if (canvas.DraftDocument.Length > 0)
            {
                using var document = JsonDocument.Parse(canvas.DraftDocument);
                if (document.RootElement.TryGetProperty("title", out var name)) title = name.GetString() ?? "";
            }
            paintings.Add(new ApartmentPainting { Id = GetNetEntity(uid).ToString(), Title = title.Length == 0 ? "Без названия" : title,
                Preview = preview, Framed = _containers.TryGetContainingContainer(uid, out var container) && HasComp<ArtFrameComponent>(container.Owner) });
        }
        return paintings.OrderByDescending(p => p.Framed).ThenBy(p => p.Title).ThenBy(p => p.Id).ToList();
    }
}
