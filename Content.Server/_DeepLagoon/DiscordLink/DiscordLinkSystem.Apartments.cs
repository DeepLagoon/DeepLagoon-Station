using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Administration;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.DiscordLink;

public sealed partial class DiscordLinkSystem
{
    private async Task<ApiResult> DiscordApartments(ModerationRequest request, NetUserId uid, NetUserId actor)
    {
        var author = await _database.GetAdminDataForAsync(actor);
        if (author is not { Suspended: false } || (EffectiveAdminFlags(author) & AdminFlags.Host) == 0)
            return ModerationError("apartment_host_required");
        if (request.Operation == "apartment_catalog")
            return new ApiResult(HttpStatusCode.OK, new { rows = _prototypes.EnumeratePrototypes<ApartmentTemplatePrototype>()
                .OrderBy(item => item.ID).Select(item => new { id = item.ID, name = Loc.GetString(item.Name),
                    price = item.Price, available = item.Available && item.Map != null && item.Price >= 0,
                    map = item.Map, width = item.Width, height = item.Height, version = item.Version }).ToArray() });
        if (request.Template is not { Length: > 0 and <= 128 } || request.Message is not { Length: > 0 and <= 500 } ||
            string.IsNullOrWhiteSpace(request.Message)) return ModerationError("invalid_request");
        if (await _database.GetPlayerRecordByUserId(uid) == null) return ModerationError("player_not_in_slot");
        var apartments = EntityManager.System<ApartmentSystem>();
        var error = apartments.GrantTemplate(uid, request.Template, out var alreadyOwned);
        if (error != null) return ModerationError(error);
        Log.Info($"Discord apartment grant: target={uid} actor={actor} template={request.Template} alreadyOwned={alreadyOwned} reason={request.Message.Replace('\r', ' ').Replace('\n', ' ')}");
        return new ApiResult(HttpStatusCode.OK, new { ok = true, uid = uid.ToString(), template = request.Template, already_owned = alreadyOwned });
    }
}
